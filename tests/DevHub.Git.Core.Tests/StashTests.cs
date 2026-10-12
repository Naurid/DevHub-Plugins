using DevHub.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Git.Core.Tests;

public class StashTests
{
    [Theory]
    [InlineData("On main: half-done login", "main", "half-done login")]
    [InlineData("WIP on feature/x: 1a2b3c4 Fix it", "feature/x", "WIP: 1a2b3c4 Fix it")]
    [InlineData("On (no branch): detached work", null, "detached work")]
    [InlineData("something else", null, "something else")]
    public void Stash_subjects_are_split_into_branch_and_message(string subject, string? branch, string message) =>
        Assert.Equal((branch, message), GitStash.Parse(subject));

    private static string Read(TempGitRepo repo, string path) =>
        File.ReadAllText(Path.Combine(repo.Path, path)).Replace("\r\n", "\n");

    private static TempGitRepo RepoWithChanges()
    {
        var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "one\n");
        repo.CommitAll("first");
        repo.WriteFile("a.txt", "one\nchanged\n");
        repo.WriteFile("new.txt", "brand new\n");
        return repo;
    }

    [GitFact]
    public async Task Stashing_puts_changes_aside_and_lists_them_with_their_files()
    {
        using var repo = RepoWithChanges();

        Assert.True(await repo.Client.StashAsync("my work", includeUntracked: true));

        Assert.Empty((await repo.Client.GetStatusAsync()).Changes);
        var stash = Assert.Single(await repo.Client.GetStashesAsync());
        Assert.Equal("stash@{0}", stash.Ref);
        Assert.Equal("my work", stash.Message);
        Assert.Equal("main", stash.Branch);
        Assert.True(stash.HasUntracked);

        var files = await repo.Client.GetStashChangesAsync(stash);
        Assert.Equal(["a.txt", "new.txt"], files.Select(f => f.Change.Path));
        // Each file's diff comes from its own part of the stash.
        var diff = await repo.Client.GetCommitDiffAsync(files[1].Source, files[1].Change);
        Assert.Contains(diff.Hunks.SelectMany(h => h.Lines), l => l.Text == "brand new");
    }

    [GitFact]
    public async Task Nothing_to_stash_returns_false_and_makes_no_stash()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "one\n");
        repo.CommitAll("first");

        Assert.False(await repo.Client.StashAsync("nothing"));
        Assert.Empty(await repo.Client.GetStashesAsync());
    }

    [GitFact]
    public async Task Without_untracked_files_new_files_stay_in_the_working_tree()
    {
        using var repo = RepoWithChanges();

        await repo.Client.StashAsync(includeUntracked: false);

        var change = Assert.Single((await repo.Client.GetStatusAsync()).Changes);
        Assert.Equal("new.txt", change.Path);
        Assert.False((await repo.Client.GetStashesAsync())[0].HasUntracked);
    }

    [GitFact]
    public async Task Apply_keeps_the_stash_and_pop_removes_it()
    {
        using var repo = RepoWithChanges();
        await repo.Client.StashAsync("work");
        var stash = (await repo.Client.GetStashesAsync())[0];

        Assert.Equal(StashApplyResult.Applied, await repo.Client.ApplyStashAsync(stash, pop: false));
        Assert.Equal("one\nchanged\n", Read(repo, "a.txt"));
        Assert.Single(await repo.Client.GetStashesAsync());

        repo.Run("checkout", "--", "a.txt");
        File.Delete(Path.Combine(repo.Path, "new.txt"));
        Assert.Equal(StashApplyResult.Applied, await repo.Client.ApplyStashAsync(stash, pop: true));
        Assert.Equal("brand new\n", Read(repo, "new.txt"));
        Assert.Empty(await repo.Client.GetStashesAsync());
    }

    [GitFact]
    public async Task Operations_find_the_stash_by_id_even_after_its_index_moved()
    {
        using var repo = RepoWithChanges();
        await repo.Client.StashAsync("older");
        var older = (await repo.Client.GetStashesAsync())[0]; // stash@{0} now…
        repo.WriteFile("a.txt", "newer\n");
        await repo.Client.StashAsync("newer"); // …stash@{1} after this

        await repo.Client.DropStashAsync(older);

        Assert.Equal("newer", Assert.Single(await repo.Client.GetStashesAsync()).Message);
    }

    [GitFact]
    public async Task A_pop_that_conflicts_marks_the_conflict_and_keeps_the_stash()
    {
        using var repo = RepoWithChanges();
        await repo.Client.StashAsync("work", includeUntracked: false);
        var stash = (await repo.Client.GetStashesAsync())[0];
        repo.WriteFile("a.txt", "one\nother\n");
        repo.CommitAll("clashing commit");

        Assert.Equal(StashApplyResult.Conflicts, await repo.Client.ApplyStashAsync(stash, pop: true));

        Assert.Contains((await repo.Client.GetStatusAsync()).Changes, c => c.Path == "a.txt" && c.IsConflicted);
        Assert.Single(await repo.Client.GetStashesAsync());
    }

    [GitFact]
    public async Task Stashing_with_a_conflicted_file_explains_why_it_cant()
    {
        using var repo = RepoWithChanges();
        await repo.Client.StashAsync("work", includeUntracked: false);
        var stash = (await repo.Client.GetStashesAsync())[0];
        repo.WriteFile("a.txt", "one\nother\n");
        repo.CommitAll("clashing commit");
        await repo.Client.ApplyStashAsync(stash, pop: false); // leaves a.txt in conflict

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Client.StashAsync("again"));

        Assert.Contains("a.txt has unresolved conflicts", ex.Message);
        Assert.Single(await repo.Client.GetStashesAsync());
    }

    [GitFact]
    public async Task Apply_refuses_when_local_changes_would_be_overwritten()
    {
        using var repo = RepoWithChanges();
        await repo.Client.StashAsync("work", includeUntracked: false);
        var stash = (await repo.Client.GetStashesAsync())[0];
        repo.WriteFile("a.txt", "one\nlocal edit\n");

        await Assert.ThrowsAsync<GitException>(() => repo.Client.ApplyStashAsync(stash, pop: true));

        Assert.Equal("one\nlocal edit\n", Read(repo, "a.txt"));
        Assert.Single(await repo.Client.GetStashesAsync());
    }
}
