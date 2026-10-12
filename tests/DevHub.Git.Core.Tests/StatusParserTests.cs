using DevHub.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Git.Core.Tests;

/// <summary>Parser tests on captured output (fast, no git needed, covers odd cases).</summary>
public class StatusParserTests
{
    private const string Oid = "1111111111111111111111111111111111111111";
    private const string Hash = "2222222222222222222222222222222222222222";

    private static string Z(params string[] entries) => string.Join('\0', entries) + '\0';

    [Fact]
    public void Reads_branch_upstream_and_ahead_behind()
    {
        var status = StatusParser.Parse(Z(
            $"# branch.oid {Oid}", "# branch.head main", "# branch.upstream origin/main", "# branch.ab +3 -1"));

        Assert.Equal("main", status.Branch);
        Assert.Equal(Oid, status.CommitId);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(3, status.Ahead);
        Assert.Equal(1, status.Behind);
        Assert.False(status.IsDetached);
        Assert.Empty(status.Changes);
    }

    [Fact]
    public void Detached_head_has_no_branch_and_shows_the_short_commit()
    {
        var status = StatusParser.Parse(Z($"# branch.oid {Oid}", "# branch.head (detached)"));

        Assert.Null(status.Branch);
        Assert.True(status.IsDetached);
        Assert.Equal("1111111", status.DisplayBranch);
    }

    [Fact]
    public void New_repository_has_a_branch_but_no_commit()
    {
        var status = StatusParser.Parse(Z("# branch.oid (initial)", "# branch.head main"));

        Assert.Equal("main", status.Branch);
        Assert.Null(status.CommitId);
        Assert.False(status.IsDetached);
    }

    [Fact]
    public void Ordinary_change_keeps_spaces_in_the_path()
    {
        var status = StatusParser.Parse(Z($"1 .M N... 100644 100644 100644 {Hash} {Hash} docs/my notes.md"));

        var change = Assert.Single(status.Changes);
        Assert.Equal("docs/my notes.md", change.Path);
        Assert.Equal('.', change.IndexStatus);
        Assert.Equal('M', change.WorkTreeStatus);
        Assert.True(change.IsUnstaged);
        Assert.False(change.IsStaged);
        Assert.Equal("my notes.md", change.FileName);
        Assert.Equal("docs", change.Directory);
    }

    [Fact]
    public void Staged_and_then_edited_again_is_both_staged_and_unstaged()
    {
        var change = Assert.Single(StatusParser.Parse(Z($"1 MM N... 100644 100644 100644 {Hash} {Hash} a.txt")).Changes);

        Assert.True(change.IsStaged);
        Assert.True(change.IsUnstaged);
    }

    [Fact]
    public void Rename_reads_the_original_path_from_the_next_entry()
    {
        var status = StatusParser.Parse(Z(
            $"2 R. N... 100644 100644 100644 {Hash} {Hash} R100 new name.txt", "old name.txt",
            "? after.txt"));

        Assert.Equal(2, status.Changes.Count);
        var rename = status.Changes[0];
        Assert.Equal(FileChangeKind.Renamed, rename.Kind);
        Assert.Equal("new name.txt", rename.Path);
        Assert.Equal("old name.txt", rename.OriginalPath);
        Assert.True(rename.IsStaged);
        Assert.Equal("after.txt", status.Changes[1].Path);
    }

    [Fact]
    public void Unmerged_and_untracked_entries()
    {
        var status = StatusParser.Parse(Z(
            $"u UU N... 100644 100644 100644 100644 {Hash} {Hash} {Hash} conflict.txt",
            "? new folder/",
            "? ünïcödé.txt"));

        Assert.True(status.Changes[0].IsConflicted);
        Assert.False(status.Changes[0].IsStaged);
        Assert.Equal(FileChangeKind.Untracked, status.Changes[1].Kind);
        Assert.Equal("new folder/", status.Changes[1].Path);
        Assert.Equal("new folder", status.Changes[1].FileName);
        Assert.True(status.Changes[1].IsUnstaged);
        Assert.Equal("ünïcödé.txt", status.Changes[2].Path);
    }

    [Fact]
    public void Empty_output_is_a_clean_status()
    {
        var status = StatusParser.Parse("");

        Assert.Empty(status.Changes);
        Assert.Null(status.Branch);
    }
}

/// <summary>The same, against real git: the parser must agree with what git actually prints.</summary>
public class GitStatusTests
{
    [GitFact]
    public async Task Clean_repository_has_no_changes()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1");
        repo.CommitAll("first");

        var status = await repo.Client.GetStatusAsync();

        Assert.Equal("main", status.Branch);
        Assert.NotNull(status.CommitId);
        Assert.Empty(status.Changes);
    }

    [GitFact]
    public async Task Modified_staged_deleted_untracked_and_unicode_paths()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("modified.txt", "1");
        repo.WriteFile("deleted.txt", "1");
        repo.WriteFile("staged.txt", "1");
        repo.CommitAll("first");

        repo.WriteFile("modified.txt", "2");
        File.Delete(Path.Combine(repo.Path, "deleted.txt"));
        repo.WriteFile("staged.txt", "2");
        repo.Run("add", "staged.txt");
        repo.WriteFile("new dir/ünïcödé file.txt", "x");

        var changes = (await repo.Client.GetStatusAsync()).Changes.ToDictionary(c => c.Path);

        Assert.Equal('M', changes["modified.txt"].WorkTreeStatus);
        Assert.Equal('D', changes["deleted.txt"].WorkTreeStatus);
        Assert.True(changes["staged.txt"].IsStaged);
        Assert.False(changes["staged.txt"].IsUnstaged);
        // "normal" untracked mode: a new folder is one entry; quotepath=false keeps unicode readable.
        Assert.Equal(FileChangeKind.Untracked, changes["new dir/"].Kind);
    }

    [GitFact]
    public async Task Staged_rename_is_reported_with_its_original_path()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("old name.txt", "same content\n");
        repo.CommitAll("first");
        repo.Run("mv", "old name.txt", "new name.txt");

        var change = Assert.Single((await repo.Client.GetStatusAsync()).Changes);

        Assert.Equal(FileChangeKind.Renamed, change.Kind);
        Assert.Equal("new name.txt", change.Path);
        Assert.Equal("old name.txt", change.OriginalPath);
    }

    [GitFact]
    public async Task Ahead_and_behind_an_upstream_branch()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1");
        repo.CommitAll("base");
        repo.Run("branch", "other");
        repo.WriteFile("a.txt", "2");
        repo.CommitAll("ahead 1");
        repo.WriteFile("a.txt", "3");
        repo.CommitAll("ahead 2");
        repo.Run("switch", "-q", "other");
        repo.WriteFile("b.txt", "1");
        repo.CommitAll("other's commit");
        repo.Run("switch", "-q", "main");
        repo.Run("branch", "-u", "other");

        var status = await repo.Client.GetStatusAsync();

        Assert.Equal("other", status.Upstream);
        Assert.Equal(2, status.Ahead);
        Assert.Equal(1, status.Behind);
    }

    [GitFact]
    public async Task Merge_conflict_is_unmerged_and_the_operation_is_merging()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("conflict.txt", "base\n");
        repo.CommitAll("base");
        repo.Run("switch", "-q", "-c", "theirs");
        repo.WriteFile("conflict.txt", "theirs\n");
        repo.CommitAll("theirs");
        repo.Run("switch", "-q", "main");
        repo.WriteFile("conflict.txt", "ours\n");
        repo.CommitAll("ours");
        await repo.Runner.RunAsync(repo.Path, ["merge", "theirs"], new GitRunOptions { AllowFailure = true });

        var change = Assert.Single((await repo.Client.GetStatusAsync()).Changes);

        Assert.True(change.IsConflicted);
        Assert.Equal(RepositoryOperation.Merging, await repo.Client.GetOperationAsync());
    }

    [GitFact]
    public async Task No_operation_in_a_normal_repository()
    {
        using var repo = new TempGitRepo();

        Assert.Equal(RepositoryOperation.None, await repo.Client.GetOperationAsync());
    }
}
