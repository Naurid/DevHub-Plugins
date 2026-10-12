using DevHub.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Git.Core.Tests;

public class RemotesTests
{
    [Fact]
    public void Remote_list_output_is_parsed_with_fetch_and_push_urls()
    {
        var output =
            "origin\thttps://github.com/me/app.git (fetch)\n" +
            "origin\thttps://github.com/me/app.git (push)\n" +
            "mirror\tC:\\remotes\\my app.git (fetch)\n" +
            "mirror\tssh://backup/app.git (push)\n";

        var remotes = GitRepositoryClient.ParseRemotes(output);

        Assert.Equal(
            [
                new GitRemote("origin", "https://github.com/me/app.git", "https://github.com/me/app.git"),
                new GitRemote("mirror", "C:\\remotes\\my app.git", "ssh://backup/app.git"),
            ],
            remotes);
    }

    private static (TempGitRepo Repo, TempGitRepo Bare) RepoAndBare()
    {
        var bare = new TempGitRepo(init: false);
        bare.Run("init", "--bare", "-q", "-b", "main");
        var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1\n");
        repo.CommitAll("first");
        return (repo, bare);
    }

    [GitFact]
    public async Task Remotes_are_added_renamed_edited_and_removed()
    {
        var (repo, bare) = RepoAndBare();
        using var _ = repo;
        using var __ = bare;

        await repo.Client.AddRemoteAsync("origin", bare.Path + "  ");
        Assert.Equal([new GitRemote("origin", bare.Path, bare.Path)], await repo.Client.GetRemoteDetailsAsync());

        await repo.Client.FetchAsync();
        await repo.Client.PushAsync(newUpstreamRemote: "origin");
        await repo.Client.RenameRemoteAsync("origin", "upstream");
        var branch = (await repo.Client.GetBranchesAsync()).Single(b => b.Name == "main" && !b.IsRemote);
        Assert.Equal("upstream/main", branch.Upstream); // tracking follows the rename

        await repo.Client.SetRemoteUrlAsync("upstream", "https://example.com/other.git");
        Assert.Equal("https://example.com/other.git", (await repo.Client.GetRemoteDetailsAsync())[0].FetchUrl);

        await repo.Client.RemoveRemoteAsync("upstream");
        Assert.Empty(await repo.Client.GetRemoteDetailsAsync());
        Assert.DoesNotContain(await repo.Client.GetBranchesAsync(), b => b.IsRemote);
    }

    [GitFact]
    public async Task Fetching_an_empty_remote_into_a_repository_without_commits_succeeds_with_no_branches()
    {
        using var bare = new TempGitRepo(init: false);
        bare.Run("init", "--bare", "-q", "-b", "main");
        using var repo = new TempGitRepo();
        await repo.Client.AddRemoteAsync("origin", bare.Path);

        await repo.Client.FetchAsync();

        Assert.Equal(["origin"], await repo.Client.GetRemotesAsync());
        Assert.DoesNotContain(await repo.Client.GetBranchesAsync(), b => b.IsRemote);
        var status = await repo.Client.GetStatusAsync();
        Assert.Null(status.CommitId);
        Assert.Null(status.Upstream);
    }

    [GitTheory]
    [InlineData("origin", true)]
    [InlineData("my-fork_2", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("-x", false)]
    [InlineData("a/b", false)]
    [InlineData("bad..name", false)]
    public async Task Remote_names_follow_gits_rules(string name, bool valid)
    {
        using var repo = new TempGitRepo();
        Assert.Equal(valid, await repo.Client.IsValidRemoteNameAsync(name));
    }

    [Theory]
    [InlineData("https://github.com/me/my-app.git", "my-app")]
    [InlineData("https://github.com/me/my-app", "my-app")]
    [InlineData("git@github.com:me/tool.git", "tool")]
    [InlineData("C:\\repos\\thing.git\\", "thing")]
    [InlineData("https://github.com/", "github.com")]
    public void Clone_folder_names_match_gits(string url, string expected) =>
        Assert.Equal(expected, GitCloner.FolderNameFor(url));

    [GitFact]
    public async Task A_repository_is_cloned_into_a_new_folder()
    {
        var (repo, bare) = RepoAndBare();
        using var _ = repo;
        using var __ = bare;
        await repo.Client.AddRemoteAsync("origin", bare.Path);
        await repo.Client.PushAsync(newUpstreamRemote: "origin");
        var target = Path.Combine(Path.GetDirectoryName(repo.Path)!, "clone", "app");

        await GitCloner.CloneAsync(repo.Runner, bare.Path, target);

        Assert.Equal("1\n", File.ReadAllText(Path.Combine(target, "a.txt")).Replace("\r\n", "\n"));
        var cloned = new GitRepositoryClient(repo.Runner, target);
        Assert.Equal("origin", (await cloned.GetRemoteDetailsAsync()).Single().Name);
    }

    [GitFact]
    public async Task Cloning_into_a_folder_with_files_is_refused()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => GitCloner.CloneAsync(repo.Runner, "unused", repo.Path));
    }

    [GitFact]
    public async Task A_failing_clone_is_explained()
    {
        using var repo = new TempGitRepo();
        var missing = Path.Combine(Path.GetDirectoryName(repo.Path)!, "no-such-remote.git");

        var ex = await Assert.ThrowsAsync<RemoteOperationException>(() =>
            GitCloner.CloneAsync(repo.Runner, missing, Path.Combine(Path.GetDirectoryName(repo.Path)!, "c")));

        Assert.False(string.IsNullOrWhiteSpace(ex.Error.Explanation));
    }
}
