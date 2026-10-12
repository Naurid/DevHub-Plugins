using DevHub.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Git.Core.Tests;

public class CherryPickRevertTests
{
    private static string Read(TempGitRepo repo, string path) =>
        File.ReadAllText(Path.Combine(repo.Path, path)).Replace("\r\n", "\n");

    /// <summary>HEAD in a git format ("%H" id, "%s" subject). (GetLogAsync lists every branch, so its first commit isn't always HEAD.)</summary>
    private static string Head(TempGitRepo repo, string format) => repo.Run("log", "-1", "--format=" + format).StandardOutput.Trim();

    private static async Task<GitCommit> CommitAsync(TempGitRepo repo, string subject) =>
        (await repo.Client.GetLogAsync(100)).First(c => c.Subject == subject);

    /// <summary>main: base. feature: base → "add b" (new file b.txt) → "edit a" (a.txt line 1). Checked out: main.</summary>
    private static TempGitRepo RepoWithFeature()
    {
        var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "base\nshared\n");
        repo.CommitAll("base");
        repo.Run("checkout", "-q", "-b", "feature");
        repo.WriteFile("b.txt", "b\n");
        repo.CommitAll("add b");
        repo.WriteFile("a.txt", "feature\nshared\n");
        repo.CommitAll("edit a");
        repo.Run("checkout", "-q", "main");
        return repo;
    }

    [GitFact]
    public async Task Cherry_pick_copies_one_commit_onto_the_current_branch()
    {
        using var repo = RepoWithFeature();

        Assert.Equal(OperationOutcome.Done, await repo.Client.CherryPickAsync(await CommitAsync(repo, "add b")));

        Assert.Equal("add b", Head(repo, "%s"));
        Assert.True(File.Exists(Path.Combine(repo.Path, "b.txt")));
        Assert.Equal("base\nshared\n", Read(repo, "a.txt")); // the other feature commit isn't here
    }

    [GitFact]
    public async Task A_conflicting_cherry_pick_stops_and_can_be_continued()
    {
        using var repo = RepoWithFeature();
        repo.WriteFile("a.txt", "main\nshared\n");
        repo.CommitAll("main edit");

        Assert.Equal(OperationOutcome.Conflicts, await repo.Client.CherryPickAsync(await CommitAsync(repo, "edit a")));
        Assert.Equal(RepositoryOperation.CherryPicking, await repo.Client.GetOperationAsync());

        var conflict = (await repo.Client.GetStatusAsync()).Changes.Single(c => c.IsConflicted);
        await repo.Client.ResolveAsync(conflict, ConflictChoice.Theirs);
        Assert.Equal(OperationOutcome.Done, await repo.Client.ContinueAsync());

        Assert.Equal("feature\nshared\n", Read(repo, "a.txt"));
        Assert.Equal("edit a", Head(repo, "%s"));
        Assert.Equal(RepositoryOperation.None, await repo.Client.GetOperationAsync());
    }

    [GitFact]
    public async Task Cherry_picking_changes_already_on_the_branch_changes_nothing()
    {
        using var repo = RepoWithFeature();
        var addB = await CommitAsync(repo, "add b");
        await repo.Client.CherryPickAsync(addB);
        var head = Head(repo, "%H");

        Assert.Equal(OperationOutcome.UpToDate, await repo.Client.CherryPickAsync(addB));

        Assert.Equal(head, Head(repo, "%H"));
        Assert.Equal(RepositoryOperation.None, await repo.Client.GetOperationAsync());
    }

    [GitFact]
    public async Task Revert_adds_a_commit_that_undoes_one()
    {
        using var repo = RepoWithFeature();
        repo.Run("checkout", "-q", "feature");

        Assert.Equal(OperationOutcome.Done, await repo.Client.RevertAsync(await CommitAsync(repo, "add b")));

        Assert.Equal("Revert \"add b\"", Head(repo, "%s"));
        Assert.False(File.Exists(Path.Combine(repo.Path, "b.txt")));
        Assert.Equal("feature\nshared\n", Read(repo, "a.txt")); // the later commit stays
    }

    [GitFact]
    public async Task A_merge_commit_is_reverted_against_its_first_parent()
    {
        using var repo = RepoWithFeature();
        repo.Run("merge", "--no-ff", "-q", "--no-edit", "feature");
        var merge = (await repo.Client.GetLogAsync(100)).First(c => c.Id == Head(repo, "%H"));
        Assert.True(merge.IsMerge);

        Assert.Equal(OperationOutcome.Done, await repo.Client.RevertAsync(merge));

        Assert.False(File.Exists(Path.Combine(repo.Path, "b.txt")));
        Assert.Equal("base\nshared\n", Read(repo, "a.txt"));
    }

    [GitFact]
    public async Task A_conflicting_revert_can_be_aborted()
    {
        using var repo = RepoWithFeature();
        repo.Run("checkout", "-q", "feature");
        repo.WriteFile("a.txt", "later\nshared\n");
        repo.CommitAll("later edit");
        var head = Head(repo, "%H");

        Assert.Equal(OperationOutcome.Conflicts, await repo.Client.RevertAsync(await CommitAsync(repo, "edit a")));
        Assert.Equal(RepositoryOperation.Reverting, await repo.Client.GetOperationAsync());

        await repo.Client.AbortAsync();
        Assert.Equal(head, Head(repo, "%H"));
        Assert.Equal("later\nshared\n", Read(repo, "a.txt"));
    }
}
