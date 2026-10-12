using DevHub.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Git.Core.Tests;

public class ConflictTests
{
    [Theory]
    [InlineData("a\n<<<<<<< HEAD\nmine\n=======\ntheirs\n>>>>>>> other\nb\n", true)]
    [InlineData("<<<<<<< HEAD\r\nmine\r\n=======\r\ntheirs\r\n>>>>>>> other\r\n", true)]
    [InlineData("just text\nwith <<<<<<< in the middle\n", false)]
    [InlineData(">>>>>>> before\n<<<<<<< after\n", false)]
    public void Conflict_markers_are_recognised(string text, bool expected) =>
        Assert.Equal(expected, ConflictMarkers.Contains(text));

    /// <summary>
    /// main and feature both change line 1 of f.txt: main → "main", feature → "feature".
    /// Checked out: main.
    /// </summary>
    private static TempGitRepo DivergedRepo()
    {
        var repo = new TempGitRepo();
        repo.WriteFile("f.txt", "base\nshared\n");
        repo.CommitAll("base");
        repo.Run("checkout", "-q", "-b", "feature");
        repo.WriteFile("f.txt", "feature\nshared\n");
        repo.CommitAll("feature change");
        repo.Run("checkout", "-q", "main");
        repo.WriteFile("f.txt", "main\nshared\n");
        repo.CommitAll("main change");
        return repo;
    }

    private static string Read(TempGitRepo repo, string path) =>
        File.ReadAllText(Path.Combine(repo.Path, path)).Replace("\r\n", "\n");

    private static async Task<FileChange> Conflict(TempGitRepo repo, string path) =>
        (await repo.Client.GetStatusAsync()).Changes.Single(c => c.Path == path && c.IsConflicted);

    [GitFact]
    public async Task A_conflicting_merge_stops_and_accepting_yours_then_continuing_commits_it()
    {
        using var repo = DivergedRepo();

        Assert.Equal(OperationOutcome.Conflicts, await repo.Client.MergeAsync("feature"));
        Assert.Equal(RepositoryOperation.Merging, await repo.Client.GetOperationAsync());
        Assert.True(await repo.Client.HasConflictMarkersAsync("f.txt"));

        await repo.Client.ResolveAsync(await Conflict(repo, "f.txt"), ConflictChoice.Yours);

        Assert.Equal("main\nshared\n", Read(repo, "f.txt"));
        Assert.DoesNotContain((await repo.Client.GetStatusAsync()).Changes, c => c.IsConflicted);

        Assert.Equal(OperationOutcome.Done, await repo.Client.ContinueAsync());
        Assert.Equal(RepositoryOperation.None, await repo.Client.GetOperationAsync());
        var head = (await repo.Client.GetLogAsync(1))[0];
        Assert.Equal(2, head.ParentIds.Count); // a merge commit
        Assert.StartsWith("Merge branch 'feature'", head.Subject);
    }

    [GitFact]
    public async Task Accepting_theirs_in_a_merge_takes_the_merged_branchs_version()
    {
        using var repo = DivergedRepo();
        await repo.Client.MergeAsync("feature");

        await repo.Client.ResolveAsync(await Conflict(repo, "f.txt"), ConflictChoice.Theirs);

        Assert.Equal("feature\nshared\n", Read(repo, "f.txt"));
    }

    [GitFact]
    public async Task In_a_rebase_yours_means_the_commit_being_replayed()
    {
        using var repo = DivergedRepo();
        repo.Run("checkout", "-q", "feature");

        Assert.Equal(OperationOutcome.Conflicts, await repo.Client.RebaseAsync("main"));
        Assert.Equal(RepositoryOperation.Rebasing, await repo.Client.GetOperationAsync());

        // Yours = feature's change (git calls it "theirs" during a rebase).
        await repo.Client.ResolveAsync(await Conflict(repo, "f.txt"), ConflictChoice.Yours);
        Assert.Equal("feature\nshared\n", Read(repo, "f.txt"));

        Assert.Equal(OperationOutcome.Done, await repo.Client.ContinueAsync());
        var log = await repo.Client.GetLogAsync(10);
        Assert.Equal("feature change", log[0].Subject);
        Assert.Equal("main change", log[1].Subject); // replayed on top of main: linear
        Assert.Single(log[0].ParentIds);
    }

    [GitFact]
    public async Task Aborting_a_merge_puts_everything_back()
    {
        using var repo = DivergedRepo();
        var before = (await repo.Client.GetLogAsync(1))[0].Id;
        await repo.Client.MergeAsync("feature");

        await repo.Client.AbortAsync();

        Assert.Equal(RepositoryOperation.None, await repo.Client.GetOperationAsync());
        Assert.Equal("main\nshared\n", Read(repo, "f.txt"));
        Assert.Equal(before, (await repo.Client.GetLogAsync(1))[0].Id);
    }

    [GitFact]
    public async Task Skipping_drops_the_conflicting_commit_from_the_rebase()
    {
        using var repo = DivergedRepo();
        repo.Run("checkout", "-q", "feature");
        await repo.Client.RebaseAsync("main");

        Assert.Equal(OperationOutcome.Done, await repo.Client.SkipAsync());

        Assert.Equal("main change", (await repo.Client.GetLogAsync(1))[0].Subject);
        Assert.Equal("main\nshared\n", Read(repo, "f.txt"));
    }

    [GitFact]
    public async Task Merges_without_conflicts_finish_or_report_up_to_date()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "a\n");
        repo.CommitAll("base");
        repo.Run("checkout", "-q", "-b", "feature");
        repo.WriteFile("b.txt", "b\n");
        repo.CommitAll("feature");
        repo.Run("checkout", "-q", "main");

        Assert.Equal(OperationOutcome.Done, await repo.Client.MergeAsync("feature"));
        Assert.True(File.Exists(Path.Combine(repo.Path, "b.txt")));
        Assert.Equal(OperationOutcome.UpToDate, await repo.Client.MergeAsync("feature"));
    }

    [GitFact]
    public async Task Keeping_the_side_that_deleted_the_file_removes_it()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("f.txt", "base\n");
        repo.CommitAll("base");
        repo.Run("checkout", "-q", "-b", "feature");
        repo.Run("rm", "-q", "f.txt");
        repo.CommitAll("delete on feature");
        repo.Run("checkout", "-q", "main");
        repo.WriteFile("f.txt", "changed on main\n");
        repo.CommitAll("change on main");

        Assert.Equal(OperationOutcome.Conflicts, await repo.Client.MergeAsync("feature"));
        var conflict = await Conflict(repo, "f.txt"); // modify/delete: "UD"

        await repo.Client.ResolveAsync(conflict, ConflictChoice.Theirs);

        Assert.False(File.Exists(Path.Combine(repo.Path, "f.txt")));
        Assert.Equal(OperationOutcome.Done, await repo.Client.ContinueAsync());
    }

    [GitFact]
    public async Task The_configured_merge_tool_runs_and_marks_the_file_resolved()
    {
        using var repo = DivergedRepo();
        Assert.Null(await repo.Client.GetMergeToolAsync());

        // A stand-in "tool" that takes the incoming version (git runs it with sh).
        repo.Run("config", "merge.tool", "fake");
        repo.Run("config", "mergetool.fake.cmd", "cp \"$REMOTE\" \"$MERGED\"");
        repo.Run("config", "mergetool.fake.trustExitCode", "true");
        await repo.Client.MergeAsync("feature");

        Assert.Equal("fake", await repo.Client.GetMergeToolAsync());
        await repo.Client.RunMergeToolAsync("f.txt");

        Assert.Equal("feature\nshared\n", Read(repo, "f.txt"));
        Assert.DoesNotContain((await repo.Client.GetStatusAsync()).Changes, c => c.IsConflicted);
        Assert.False(File.Exists(Path.Combine(repo.Path, "f.txt.orig")));
    }
}
