using DevHub.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Git.Core.Tests;

public class TagTests
{
    [Fact]
    public void Annotated_and_lightweight_tags_are_parsed()
    {
        var output =
            "refs/tags/v2\0tag\0aaaa\0cccc\01767268800\0Release 2\n" +
            "refs/tags/v1\0commit\0bbbb\0\01767182400\0first commit\n";

        var tags = TagParser.Parse(output);

        Assert.Equal(2, tags.Count);
        Assert.Equal(("v2", "cccc", true, "Release 2"), (tags[0].Name, tags[0].CommitId, tags[0].IsAnnotated, tags[0].Subject));
        Assert.Equal(("v1", "bbbb", false), (tags[1].Name, tags[1].CommitId, tags[1].IsAnnotated));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767182400), tags[1].Date);
    }

    [GitFact]
    public async Task Tags_are_created_listed_and_deleted()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1\n");
        repo.CommitAll("first");
        var first = (await repo.Client.GetLogAsync(1))[0].Id;
        repo.WriteFile("a.txt", "2\n");
        repo.CommitAll("second");

        await repo.Client.CreateTagAsync("v1", first);
        await repo.Client.CreateTagAsync("v2", message: "Release 2\r\n\r\nNotes");

        var tags = await repo.Client.GetTagsAsync();
        var v1 = tags.Single(t => t.Name == "v1");
        var v2 = tags.Single(t => t.Name == "v2");
        Assert.Equal(first, v1.CommitId);
        Assert.False(v1.IsAnnotated);
        Assert.Equal("first", v1.Subject);
        Assert.True(v2.IsAnnotated);
        Assert.Equal("Release 2", v2.Subject);
        Assert.Equal(await HeadAsync(repo), v2.CommitId);

        await repo.Client.DeleteTagAsync("v1");
        Assert.Equal(["v2"], (await repo.Client.GetTagsAsync()).Select(t => t.Name));
    }

    private static async Task<string> HeadAsync(TempGitRepo repo) => (await repo.Client.GetLogAsync(1))[0].Id;

    [GitTheory]
    [InlineData("v1.0", true)]
    [InlineData("release/2026-10", true)]
    [InlineData("has space", false)]
    [InlineData("-v1", false)]
    [InlineData("v1..2", false)]
    [InlineData("", false)]
    public async Task Tag_names_follow_gits_rules(string name, bool valid)
    {
        using var repo = new TempGitRepo();
        Assert.Equal(valid, await repo.Client.IsValidTagNameAsync(name));
    }

    [GitFact]
    public async Task A_tag_is_pushed_to_and_deleted_from_the_remote()
    {
        using var remote = new TempGitRepo(init: false);
        remote.Run("init", "--bare", "-b", "main");
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1\n");
        repo.CommitAll("first");
        repo.Run("remote", "add", "origin", remote.Path);
        await repo.Client.CreateTagAsync("v1");

        await repo.Client.PushTagAsync("origin", "v1");
        Assert.Contains("refs/tags/v1", remote.Run("for-each-ref", "refs/tags").StandardOutput);

        await repo.Client.DeleteRemoteTagAsync("origin", "v1");
        Assert.DoesNotContain("refs/tags/v1", remote.Run("for-each-ref", "refs/tags").StandardOutput);
        Assert.Single(await repo.Client.GetTagsAsync()); // the local one stays
    }
}
