using DevHub.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Git.Core.Tests;

public class BlameAndSubmoduleTests
{
    private const string IdA = "1111111111111111111111111111111111111111";
    private const string IdB = "2222222222222222222222222222222222222222";

    [Fact]
    public void Porcelain_blame_shares_commit_details_across_its_lines()
    {
        var output =
            $"{IdA} 1 1 2\nauthor Ann\nauthor-mail <ann@example.com>\nauthor-time 1767268800\nauthor-tz +0000\n" +
            "summary First\nfilename a.txt\n\tline one\r\n" +
            $"{IdA} 2 2\n\tline two\n" +
            $"{IdB} 3 3 1\nauthor Bob\nauthor-mail <bob@example.com>\nauthor-time 1767355200\nsummary Second\n" +
            "previous 1111111111111111111111111111111111111111 a.txt\nfilename a.txt\n\t\n";

        var lines = BlameParser.Parse(output);

        Assert.Equal([1, 2, 3], lines.Select(l => l.LineNumber));
        Assert.Equal(["line one", "line two", ""], lines.Select(l => l.Text));
        Assert.Same(lines[0].Commit, lines[1].Commit);
        Assert.Equal(("Ann", "ann@example.com", "First"), (lines[0].Commit.Author, lines[0].Commit.AuthorEmail, lines[0].Commit.Summary));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767268800), lines[0].Commit.AuthorDate);
        Assert.Equal("Bob", lines[2].Commit.Author);
    }

    [GitFact]
    public async Task Blame_shows_who_changed_each_line_including_uncommitted_ones()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "one\ntwo\n");
        repo.CommitAll("first");
        repo.WriteFile("a.txt", "one\nTWO\nthree\n");
        repo.CommitAll("second");
        repo.WriteFile("a.txt", "one\nTWO\nthree\nfour\n");

        var lines = await repo.Client.GetBlameAsync("a.txt");

        Assert.Equal(["first", "second", "second"], lines.Take(3).Select(l => l.Commit.Summary));
        Assert.True(lines[3].Commit.IsUncommitted);
        Assert.Equal("Test Author", lines[0].Commit.Author);

        // As of the first commit: two lines, both from it.
        var first = (await repo.Client.GetLogAsync(10)).Last().Id;
        var old = await repo.Client.GetBlameAsync("a.txt", first);
        Assert.Equal(["one", "two"], old.Select(l => l.Text));
        Assert.All(old, l => Assert.Equal(first, l.Commit.Id));
    }

    [Fact]
    public void Submodule_status_lines_are_parsed_including_paths_with_spaces()
    {
        var output =
            $" {IdA} libs/core (v1.0-2-g1111111)\n" +
            $"-{IdB} libs/not yet\n" +
            $"+{IdA} vendor/my lib (heads/main)\n" +
            $"U{IdB} conflicted\n";

        var subs = SubmoduleParser.Parse(output);

        Assert.Equal(
            [
                ("libs/core", SubmoduleState.UpToDate, "v1.0-2-g1111111"),
                ("libs/not yet", SubmoduleState.NotInitialized, null),
                ("vendor/my lib", SubmoduleState.DifferentCommit, "heads/main"),
                ("conflicted", SubmoduleState.Conflict, null),
            ],
            subs.Select(s => (s.Path, s.State, s.Describe)));
        Assert.Equal(IdB, subs[1].CommitId);
        Assert.Equal("my lib", subs[2].Name);
    }

    [GitFact]
    public async Task Submodules_are_listed_and_updated()
    {
        using var lib = new TempGitRepo();
        lib.WriteFile("lib.txt", "lib\n");
        lib.CommitAll("lib");
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1\n");
        repo.CommitAll("first");
        Assert.Empty(await repo.Client.GetSubmodulesAsync()); // no .gitmodules: git isn't even asked

        // Local paths as submodule URLs need protocol.file.allow (git 2.38.1+ refuses them by default).
        repo.Run("config", "--global", "protocol.file.allow", "always");
        repo.Run("submodule", "add", "-q", lib.Path, "libs/lib");
        repo.CommitAll("add submodule");

        var sub = Assert.Single(await repo.Client.GetSubmodulesAsync());
        Assert.Equal(("libs/lib", SubmoduleState.UpToDate), (sub.Path, sub.State));
        Assert.Equal(Path.Combine(repo.Path, "libs", "lib"), repo.Client.SubmoduleFolder(sub));

        // Remove the checkout, as in a fresh clone: not initialized until updated.
        repo.Run("submodule", "deinit", "-q", "-f", "libs/lib");
        Assert.Equal(SubmoduleState.NotInitialized, Assert.Single(await repo.Client.GetSubmodulesAsync()).State);

        await repo.Client.UpdateSubmodulesAsync();
        Assert.Equal(SubmoduleState.UpToDate, Assert.Single(await repo.Client.GetSubmodulesAsync()).State);
        Assert.True(File.Exists(Path.Combine(repo.Path, "libs", "lib", "lib.txt")));
    }
}
