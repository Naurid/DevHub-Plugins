using DevHub.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Git.Core.Tests;

/// <summary>Staging, unstaging, discarding and committing against real git (G3).</summary>
public class StageCommitTests
{
    private static async Task<FileChange> Single(TempGitRepo repo) =>
        Assert.Single((await repo.Client.GetStatusAsync()).Changes);

    [GitFact]
    public async Task Edit_stage_commit_loop()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1\n");
        repo.CommitAll("first");

        repo.WriteFile("a.txt", "2\n");
        Assert.True((await Single(repo)).IsUnstaged);

        await repo.Client.StageAsync(["a.txt"]);
        var staged = await Single(repo);
        Assert.True(staged.IsStaged);
        Assert.False(staged.IsUnstaged);

        var id = await repo.Client.CommitAsync("Second commit\n\nWith a body.");

        Assert.Empty((await repo.Client.GetStatusAsync()).Changes);
        Assert.Equal(40, id.Length);
        Assert.Equal("Second commit\n\nWith a body.", await repo.Client.GetLastCommitMessageAsync());
    }

    [GitFact]
    public async Task Staging_a_deletion_and_a_new_file()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("gone.txt", "x\n");
        repo.CommitAll("first");
        File.Delete(Path.Combine(repo.Path, "gone.txt"));
        repo.WriteFile("new.txt", "y\n");

        await repo.Client.StageAsync(["gone.txt", "new.txt"]);

        var changes = (await repo.Client.GetStatusAsync()).Changes;
        Assert.All(changes, c => Assert.True(c.IsStaged && !c.IsUnstaged));
        Assert.Contains(changes, c => c.Path == "gone.txt" && c.IndexStatus == 'D');
        Assert.Contains(changes, c => c.Path == "new.txt" && c.IndexStatus == 'A');
    }

    [GitFact]
    public async Task Unstage_keeps_the_working_tree_change()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1\n");
        repo.CommitAll("first");
        repo.WriteFile("a.txt", "2\n");
        repo.Run("add", "a.txt");

        await repo.Client.UnstageAsync(["a.txt"]);

        var change = await Single(repo);
        Assert.False(change.IsStaged);
        Assert.True(change.IsUnstaged);
        Assert.Equal("2\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
    }

    [GitFact]
    public async Task Unstage_works_before_the_first_commit()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1\n");
        repo.Run("add", "a.txt");

        await repo.Client.UnstageAsync(["a.txt"]);

        Assert.Equal(FileChangeKind.Untracked, (await Single(repo)).Kind);
    }

    [GitFact]
    public async Task Unstaging_a_rename_needs_both_paths()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("old.txt", "same\n");
        repo.CommitAll("first");
        repo.Run("mv", "old.txt", "new.txt");
        var rename = await Single(repo);

        await repo.Client.UnstageAsync([rename.Path, rename.OriginalPath!]);

        var changes = (await repo.Client.GetStatusAsync()).Changes;
        Assert.DoesNotContain(changes, c => c.IsStaged);
        Assert.Contains(changes, c => c.Path == "old.txt" && c.WorkTreeStatus == 'D');
        Assert.Contains(changes, c => c.Path == "new.txt" && c.Kind == FileChangeKind.Untracked);
    }

    [GitFact]
    public async Task Stage_all_and_unstage_all()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1\n");
        repo.CommitAll("first");
        repo.WriteFile("a.txt", "2\n");
        repo.WriteFile("b.txt", "new\n");

        await repo.Client.StageAllAsync();
        Assert.All((await repo.Client.GetStatusAsync()).Changes, c => Assert.True(c.IsStaged));

        await repo.Client.UnstageAllAsync();
        Assert.All((await repo.Client.GetStatusAsync()).Changes, c => Assert.False(c.IsStaged));
    }

    [GitFact]
    public async Task Paths_are_taken_literally()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("keep.txt", "x\n");
        repo.CommitAll("first");
        repo.WriteFile("a b.txt", "spaces\n");
        repo.WriteFile("other.txt", "not me\n");
        repo.WriteFile("[x].txt", "brackets\n");

        // "[x].txt" as a pattern would match "x.txt"; literally it's only itself.
        await repo.Client.StageAsync(["a b.txt", "[x].txt"]);

        var changes = (await repo.Client.GetStatusAsync()).Changes;
        Assert.True(changes.Single(c => c.Path == "a b.txt").IsStaged);
        Assert.True(changes.Single(c => c.Path == "[x].txt").IsStaged);
        Assert.Equal(FileChangeKind.Untracked, changes.Single(c => c.Path == "other.txt").Kind);
    }

    [GitFact]
    public async Task Discard_restores_tracked_files_to_the_staged_version_and_deletes_untracked()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "committed\n");
        repo.WriteFile("gone.txt", "committed\n");
        repo.CommitAll("first");
        repo.WriteFile("a.txt", "staged\n");
        repo.Run("add", "a.txt");
        repo.WriteFile("a.txt", "unstaged\n");
        File.Delete(Path.Combine(repo.Path, "gone.txt"));
        repo.WriteFile("junk.txt", "new\n");
        repo.WriteFile("newdir/x.txt", "new\n");

        var unstaged = (await repo.Client.GetStatusAsync()).Changes.Where(c => c.IsUnstaged).ToList();
        await repo.Client.DiscardAsync(unstaged);

        Assert.Equal("staged\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt"))); // staged part kept
        Assert.True(File.Exists(Path.Combine(repo.Path, "gone.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "junk.txt")));
        Assert.False(Directory.Exists(Path.Combine(repo.Path, "newdir")));
        var left = await Single(repo);
        Assert.Equal("a.txt", left.Path);
        Assert.True(left.IsStaged && !left.IsUnstaged);
    }

    [GitFact]
    public async Task Discard_refuses_conflicted_files()
    {
        var conflicted = new FileChange("f.txt", 'U', 'U', FileChangeKind.Unmerged);
        using var repo = new TempGitRepo();

        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Client.DiscardAsync([conflicted]));
    }

    [GitFact]
    public async Task Amend_replaces_the_last_commit()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1\n");
        repo.CommitAll("first");
        repo.WriteFile("a.txt", "2\n");
        repo.CommitAll("second, with a typo");
        repo.WriteFile("b.txt", "forgotten\n");
        await repo.Client.StageAsync(["b.txt"]);

        await repo.Client.CommitAsync("second", amend: true);

        Assert.Equal("2", repo.Run("rev-list", "--count", "HEAD").StandardOutput.Trim());
        Assert.Equal("second", await repo.Client.GetLastCommitMessageAsync());
        Assert.Empty((await repo.Client.GetStatusAsync()).Changes);
    }

    [GitFact]
    public async Task Message_with_crlf_quotes_and_unicode_is_kept_exactly()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1\n");
        await repo.Client.StageAllAsync();

        await repo.Client.CommitAsync("Fix \"quotes\" & ünïcödé ✓\r\n\r\n- line one\r\n- line two");

        Assert.Equal("Fix \"quotes\" & ünïcödé ✓\n\n- line one\n- line two", await repo.Client.GetLastCommitMessageAsync());
    }

    [GitFact]
    public async Task Nothing_staged_fails_with_gits_message()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1\n");
        repo.CommitAll("first");

        var ex = await Assert.ThrowsAsync<GitException>(() => repo.Client.CommitAsync("empty"));
        Assert.NotEqual(0, ex.ExitCode);
    }

    [GitFact]
    public async Task Last_commit_message_is_null_before_the_first_commit()
    {
        using var repo = new TempGitRepo();

        Assert.Null(await repo.Client.GetLastCommitMessageAsync());
    }
}
