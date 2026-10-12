using DevHub.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Git.Core.Tests;

public class LogTests
{
    [Fact]
    public void Parses_fields_parents_and_dates()
    {
        var output = "abc123\x1f" + "p1 p2\x1f" + "Ann\x1f" + "ann@x.org\x1f" + "1767268800\x1f" +
                     "Bob\x1f" + "1767272400\x1f" + "\x1f" + "Merge branch 'x'\x1e\n";

        var commit = Assert.Single(LogParser.Parse(output));

        Assert.Equal("abc123", commit.Id);
        Assert.Equal(["p1", "p2"], commit.ParentIds);
        Assert.True(commit.IsMerge);
        Assert.Equal("Ann", commit.AuthorName);
        Assert.Equal("ann@x.org", commit.AuthorEmail);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), commit.AuthorDate);
        Assert.Equal("Bob", commit.CommitterName);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 13, 0, 0, TimeSpan.Zero), commit.CommitDate);
        Assert.Equal("Merge branch 'x'", commit.Subject);
        Assert.Empty(commit.Refs);
    }

    [Fact]
    public void Ref_labels_are_classified_and_ordered()
    {
        var refs = LogParser.ParseRefs(
            "HEAD -> refs/heads/main, refs/remotes/origin/main, refs/remotes/origin/HEAD, tag: refs/tags/v1.0, refs/heads/dev");

        Assert.Equal(
        [
            new GitRefLabel(GitRefKind.LocalBranch, "main", IsHead: true),
            new GitRefLabel(GitRefKind.LocalBranch, "dev"),
            new GitRefLabel(GitRefKind.Tag, "v1.0"),
            new GitRefLabel(GitRefKind.RemoteBranch, "origin/main"),
        ], refs);
    }

    [Fact]
    public void A_detached_head_is_its_own_label()
    {
        var refs = LogParser.ParseRefs("HEAD, tag: refs/tags/v2");

        Assert.Equal(new GitRefLabel(GitRefKind.Head, "HEAD", IsHead: true), refs[0]);
        Assert.Equal(new GitRefLabel(GitRefKind.Tag, "v2"), refs[1]);
    }

    [Fact]
    public void Name_status_with_renames()
    {
        var changes = LogParser.ParseNameStatus("M\0src/a.cs\0R087\0old.txt\0new.txt\0A\0b c.txt\0D\0gone\0");

        Assert.Equal(4, changes.Count);
        Assert.Equal(new FileChange("src/a.cs", 'M', '.', FileChangeKind.Ordinary), changes[0]);
        Assert.Equal(new FileChange("new.txt", 'R', '.', FileChangeKind.Renamed, "old.txt"), changes[1]);
        Assert.Equal("b c.txt", changes[2].Path);
        Assert.Equal('D', changes[3].IndexStatus);
        Assert.All(changes, c => Assert.True(c.IsStaged));
    }

    [GitFact]
    public async Task Log_of_a_real_repository_with_a_merge_and_a_tag()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1");
        repo.CommitAll("root");
        repo.Run("checkout", "-q", "-b", "feature");
        repo.WriteFile("b.txt", "feature");
        repo.CommitAll("Feature: ünïcödé ✓");
        repo.Run("checkout", "-q", "main");
        repo.WriteFile("a.txt", "2");
        repo.CommitAll("main work");
        repo.Run("merge", "--no-ff", "-q", "-m", "Merge feature", "feature");
        repo.Run("tag", "v1");
        repo.Run("stash", "list"); // no stash: just making sure nothing odd is needed

        var log = await repo.Client.GetLogAsync(100);

        Assert.Equal(4, log.Count);
        var merge = log[0];
        Assert.Equal("Merge feature", merge.Subject);
        Assert.Equal(2, merge.ParentIds.Count);
        Assert.Contains(new GitRefLabel(GitRefKind.LocalBranch, "main", IsHead: true), merge.Refs);
        Assert.Contains(new GitRefLabel(GitRefKind.Tag, "v1"), merge.Refs);
        Assert.Contains(log, c => c.Subject == "Feature: ünïcödé ✓"
                                  && c.Refs.Contains(new GitRefLabel(GitRefKind.LocalBranch, "feature")));
        Assert.Equal("root", log[^1].Subject);
        Assert.Empty(log[^1].ParentIds);
        Assert.Equal("Test Author", merge.AuthorName);

        // Topo order: every commit comes before its parents, so the graph can be laid out.
        var position = log.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
        Assert.All(log, c => Assert.All(c.ParentIds, p => Assert.True(position[p] > position[c.Id])));
        Assert.Equal(2, CommitGraph.Layout(log).Max(r => r.LaneCount));
    }

    [GitFact]
    public async Task Stashes_are_not_in_the_log_and_max_count_limits_it()
    {
        using var repo = new TempGitRepo();
        for (var i = 1; i <= 5; i++)
        {
            repo.WriteFile("a.txt", i.ToString());
            repo.CommitAll($"c{i}");
        }
        repo.WriteFile("a.txt", "dirty");
        repo.Run("stash", "push", "-q");

        var all = await repo.Client.GetLogAsync(100);
        var newest = await repo.Client.GetLogAsync(2);

        Assert.Equal(["c5", "c4", "c3", "c2", "c1"], all.Select(c => c.Subject));
        Assert.Equal(["c5", "c4"], newest.Select(c => c.Subject));
    }

    [GitFact]
    public async Task An_empty_repository_has_an_empty_log()
    {
        using var repo = new TempGitRepo();

        Assert.Empty(await repo.Client.GetLogAsync(100));
    }

    [GitFact]
    public async Task Commit_changes_message_and_file_diff()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("keep.txt", "one\ntwo\nthree\nfour\nfive\n");
        repo.WriteFile("old name.txt", "same content\nline 2\nline 3\n");
        repo.WriteFile("gone.txt", "bye\n");
        repo.CommitAll("root");
        repo.WriteFile("keep.txt", "one\ntwo\nTHREE\nfour\nfive\n");
        repo.Run("mv", "old name.txt", "new name.txt");
        repo.Run("rm", "-q", "gone.txt");
        repo.WriteFile("dir/added.txt", "new\n");
        repo.Run("add", "-A");
        repo.Run("commit", "-q", "-m", "Second\n\nThe body,\nover two lines.");

        var log = await repo.Client.GetLogAsync(10);
        var second = log[0];
        var root = log[1];

        Assert.Equal("Second\n\nThe body,\nover two lines.", await repo.Client.GetCommitMessageAsync(second.Id));

        var changes = await repo.Client.GetCommitChangesAsync(second);
        Assert.Equal(4, changes.Count);
        Assert.Contains(changes, c => c is { Path: "keep.txt", IndexStatus: 'M' });
        Assert.Contains(changes, c => c is { Path: "new name.txt", IndexStatus: 'R', OriginalPath: "old name.txt" });
        Assert.Contains(changes, c => c is { Path: "gone.txt", IndexStatus: 'D' });
        Assert.Contains(changes, c => c is { Path: "dir/added.txt", IndexStatus: 'A' });

        var diff = await repo.Client.GetCommitDiffAsync(second, changes.First(c => c.Path == "keep.txt"));
        Assert.Equal(1, diff.AddedCount);
        Assert.Equal(1, diff.RemovedCount);
        Assert.Contains(diff.Hunks[0].Lines, l => l is { Kind: DiffLineKind.Added, Text: "THREE" });

        var rename = await repo.Client.GetCommitDiffAsync(second, changes.First(c => c.IndexStatus == 'R'));
        Assert.True(rename.IsRenamed);
        Assert.Empty(rename.Hunks);

        // The root commit compares with nothing: everything is added.
        var rootChanges = await repo.Client.GetCommitChangesAsync(root);
        Assert.Equal(3, rootChanges.Count);
        Assert.All(rootChanges, c => Assert.Equal('A', c.IndexStatus));
        var rootDiff = await repo.Client.GetCommitDiffAsync(root, rootChanges.First(c => c.Path == "keep.txt"));
        Assert.True(rootDiff.IsNewFile);
        Assert.Equal(5, rootDiff.AddedCount);
    }

    [GitFact]
    public async Task A_merge_commit_shows_what_it_brought_into_the_branch()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1");
        repo.CommitAll("root");
        repo.Run("checkout", "-q", "-b", "feature");
        repo.WriteFile("feature.txt", "f");
        repo.CommitAll("feature");
        repo.Run("checkout", "-q", "main");
        repo.WriteFile("main.txt", "m");
        repo.CommitAll("main");
        repo.Run("merge", "--no-ff", "-q", "-m", "Merge", "feature");

        var merge = (await repo.Client.GetLogAsync(10))[0];
        var changes = await repo.Client.GetCommitChangesAsync(merge);

        Assert.Equal("feature.txt", Assert.Single(changes).Path);
    }
}
