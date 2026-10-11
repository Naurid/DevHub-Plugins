using DevHub.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Git.Core.Tests;

/// <summary>Branch listing (parser + real git), checkout incl. Smart Checkout, create / rename / delete (G4).</summary>
public class BranchTests
{
    // ---- Parser ---------------------------------------------------------------------------

    [Fact]
    public void Parses_local_remote_current_tracking_and_skips_origin_head()
    {
        const string output =
            "refs/heads/main\0aaa\0*\0origin/main\0ahead 2, behind 1\01767225600\0Fix: a \"quoted\" subject\0\n" +
            "refs/heads/feature/x\0bbb\0 \0origin/feature/x\0gone\01767225601\0WIP\0\n" +
            "refs/heads/local-only\0ccc\0 \0\0\01767225602\0Local\0\n" +
            "refs/remotes/origin/HEAD\0aaa\0 \0\0\01767225600\0Fix\0refs/remotes/origin/main\n" +
            "refs/remotes/origin/feature/x\0ddd\0 \0\0\01767225603\0Remote work\0\n";

        var branches = BranchParser.Parse(output);

        Assert.Equal(["main", "feature/x", "local-only", "feature/x"], branches.Select(b => b.Name));
        var main = branches[0];
        Assert.True(main.IsCurrent);
        Assert.False(main.IsRemote);
        Assert.Equal("origin/main", main.Upstream);
        Assert.Equal((2, 1), (main.Ahead, main.Behind));
        Assert.Equal("Fix: a \"quoted\" subject", main.Subject);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767225600), main.CommitDate);

        Assert.True(branches[1].UpstreamGone);
        Assert.Null(branches[2].Upstream);

        var remote = branches[3];
        Assert.True(remote.IsRemote);
        Assert.Equal("origin", remote.Remote);
        Assert.Equal("origin/feature/x", remote.ShortName);
    }

    [Theory]
    [InlineData("", 0, 0, false)]
    [InlineData("ahead 3", 3, 0, false)]
    [InlineData("behind 4", 0, 4, false)]
    [InlineData("ahead 1, behind 12", 1, 12, false)]
    [InlineData("gone", 0, 0, true)]
    public void Tracking_info(string track, int ahead, int behind, bool gone)
    {
        Assert.Equal((ahead, behind, gone), BranchParser.ParseTrack(track));
    }

    // ---- Real git ---------------------------------------------------------------------------

    /// <summary>A repo with main (2 commits) and a "feature" branch with one more commit.</summary>
    private static TempGitRepo RepoWithFeature()
    {
        var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1\n");
        repo.CommitAll("first");
        repo.Run("branch", "feature");
        repo.Run("switch", "feature");
        repo.WriteFile("f.txt", "feature\n");
        repo.CommitAll("feature work");
        repo.Run("switch", "main");
        return repo;
    }

    [GitFact]
    public async Task Lists_local_branches_with_the_current_one_marked()
    {
        using var repo = RepoWithFeature();

        var branches = await repo.Client.GetBranchesAsync();

        Assert.Equal(["feature", "main"], branches.Select(b => b.Name).Order());
        Assert.True(branches.Single(b => b.Name == "main").IsCurrent);
        Assert.Equal("feature work", branches.Single(b => b.Name == "feature").Subject);
    }

    [GitFact]
    public async Task Remote_branches_and_tracking_from_a_clone()
    {
        using var origin = RepoWithFeature();
        using var clone = new TempGitRepo(init: false);
        clone.Run("clone", "-q", origin.Path, ".");
        clone.WriteFile("b.txt", "local\n");
        clone.CommitAll("local commit");

        var branches = await clone.Client.GetBranchesAsync();

        var main = branches.Single(b => !b.IsRemote && b.Name == "main");
        Assert.Equal("origin/main", main.Upstream);
        Assert.Equal(1, main.Ahead);
        Assert.Contains(branches, b => b.IsRemote && b.ShortName == "origin/feature");
        Assert.DoesNotContain(branches, b => b.Name == "HEAD");
    }

    [GitFact]
    public async Task Checkout_of_a_remote_branch_creates_a_tracking_branch()
    {
        using var origin = RepoWithFeature();
        using var clone = new TempGitRepo(init: false);
        clone.Run("clone", "-q", origin.Path, ".");
        var remoteFeature = (await clone.Client.GetBranchesAsync()).Single(b => b.ShortName == "origin/feature");

        Assert.Equal(CheckoutResult.Done, await clone.Client.CheckoutAsync(remoteFeature));

        var local = (await clone.Client.GetBranchesAsync()).Single(b => !b.IsRemote && b.Name == "feature");
        Assert.True(local.IsCurrent);
        Assert.Equal("origin/feature", local.Upstream);

        // Second time: the local branch exists, so it's simply switched to.
        clone.Run("switch", "main");
        Assert.Equal(CheckoutResult.Done, await clone.Client.CheckoutAsync(remoteFeature));
        Assert.Equal("feature", await clone.Client.GetBranchAsync());
    }

    [GitFact]
    public async Task Non_clashing_local_changes_come_along()
    {
        using var repo = RepoWithFeature();
        repo.WriteFile("a.txt", "edited\n"); // same on both branches → no clash
        var feature = (await repo.Client.GetBranchesAsync()).Single(b => b.Name == "feature");

        Assert.Equal(CheckoutResult.Done, await repo.Client.CheckoutAsync(feature));
        Assert.Equal("edited\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
    }

    [GitFact]
    public async Task Clashing_local_changes_block_checkout_then_smart_checkout_carries_them()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1\n2\n3\n4\n5\n");
        repo.CommitAll("first");
        repo.Run("switch", "-c", "feature");
        repo.WriteFile("a.txt", "1\n2\n3\n4\nfive\n");
        repo.CommitAll("feature edits line 5");
        repo.Run("switch", "main");
        repo.WriteFile("a.txt", "one\n2\n3\n4\n5\n"); // main edits line 1: clashes with checkout, merges cleanly
        var feature = (await repo.Client.GetBranchesAsync()).Single(b => b.Name == "feature");

        Assert.Equal(CheckoutResult.LocalChangesInTheWay, await repo.Client.CheckoutAsync(feature));
        Assert.Equal("main", await repo.Client.GetBranchAsync()); // nothing happened

        Assert.Equal(SmartCheckoutResult.Done, await repo.Client.SmartCheckoutAsync(feature));
        Assert.Equal("feature", await repo.Client.GetBranchAsync());
        Assert.Equal("one\n2\n3\n4\nfive\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
        Assert.Equal("", repo.Run("stash", "list").StandardOutput.Trim()); // stash used up
    }

    [GitFact]
    public async Task Smart_checkout_conflict_keeps_the_stash()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "base\n");
        repo.CommitAll("first");
        repo.Run("switch", "-c", "feature");
        repo.WriteFile("a.txt", "feature\n");
        repo.CommitAll("feature");
        repo.Run("switch", "main");
        repo.WriteFile("a.txt", "local\n"); // same line: re-applying conflicts
        var feature = (await repo.Client.GetBranchesAsync()).Single(b => b.Name == "feature");

        Assert.Equal(SmartCheckoutResult.ReapplyConflicted, await repo.Client.SmartCheckoutAsync(feature));
        Assert.Equal("feature", await repo.Client.GetBranchAsync());
        Assert.Contains("DevHub: local changes", repo.Run("stash", "list").StandardOutput);
    }

    [GitFact]
    public async Task Smart_checkout_with_nothing_to_stash_leaves_older_stashes_alone()
    {
        using var repo = RepoWithFeature();
        repo.WriteFile("a.txt", "old stash\n");
        repo.Run("stash", "push", "-m", "someone else's stash");
        var feature = (await repo.Client.GetBranchesAsync()).Single(b => b.Name == "feature");

        Assert.Equal(SmartCheckoutResult.Done, await repo.Client.SmartCheckoutAsync(feature));
        Assert.Contains("someone else's stash", repo.Run("stash", "list").StandardOutput);
        Assert.Equal("1\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
    }

    [GitFact]
    public async Task Create_rename_and_delete()
    {
        using var repo = RepoWithFeature();

        await repo.Client.CreateBranchAsync("topic");
        Assert.Equal("topic", await repo.Client.GetBranchAsync());

        await repo.Client.CreateBranchAsync("from-feature", startPoint: "feature", checkout: false);
        Assert.Equal("topic", await repo.Client.GetBranchAsync());
        var fromFeature = (await repo.Client.GetBranchesAsync()).Single(b => b.Name == "from-feature");
        Assert.Equal("feature work", fromFeature.Subject);

        await repo.Client.RenameBranchAsync("topic", "topic-2");
        Assert.Equal("topic-2", await repo.Client.GetBranchAsync());

        // from-feature has feature's commit, which topic-2 (= main) doesn't: unmerged, so refused.
        Assert.False(await repo.Client.DeleteBranchAsync("from-feature"));
    }

    [GitFact]
    public async Task Deleting_an_unmerged_branch_needs_force()
    {
        using var repo = RepoWithFeature(); // feature has a commit main doesn't

        Assert.False(await repo.Client.DeleteBranchAsync("feature"));
        Assert.Contains(await repo.Client.GetBranchesAsync(), b => b.Name == "feature");

        Assert.True(await repo.Client.DeleteBranchAsync("feature", force: true));
        Assert.DoesNotContain(await repo.Client.GetBranchesAsync(), b => b.Name == "feature");
    }

    [GitFact]
    public async Task Deleting_a_merged_branch_just_works()
    {
        using var repo = RepoWithFeature();
        repo.Run("branch", "merged-one"); // at main's commit: fully merged

        Assert.True(await repo.Client.DeleteBranchAsync("merged-one"));
    }

    [GitFact]
    public async Task Deleting_the_current_branch_fails_loudly()
    {
        using var repo = RepoWithFeature();

        await Assert.ThrowsAsync<GitException>(() => repo.Client.DeleteBranchAsync("main", force: true));
    }

    [GitTheory]
    [InlineData("feature/new-thing", true)]
    [InlineData("fix-123", true)]
    [InlineData("has space", false)]
    [InlineData("double..dot", false)]
    [InlineData("ends.lock", false)]
    [InlineData("-starts-with-dash", false)]
    [InlineData("", false)]
    public async Task Branch_name_rules_are_gits(string name, bool valid)
    {
        using var repo = new TempGitRepo();

        Assert.Equal(valid, await repo.Client.IsValidBranchNameAsync(name));
    }
}
