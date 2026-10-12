using DevHub.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Git.Core.Tests;

public class PartialStagingTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    /// <summary>
    /// Lines "1".."20", with some replaced (or removed: null) and some followed by extra lines.
    /// Changes at 2 and 15 are far enough apart (git keeps 3 lines of context) to be two hunks.
    /// </summary>
    private static string File20(Dictionary<int, string?>? replace = null, Dictionary<int, string[]>? insertAfter = null,
        string newline = "\n", bool finalNewline = true)
    {
        var lines = new List<string>();
        for (var n = 1; n <= 20; n++)
        {
            if (replace is null || !replace.TryGetValue(n, out var text))
                lines.Add(n.ToString());
            else if (text is not null)
                lines.Add(text);
            if (insertAfter?.GetValueOrDefault(n) is { } extra)
                lines.AddRange(extra);
        }
        return string.Join(newline, lines) + (finalNewline ? newline : "");
    }

    private static readonly string Original = File20();

    /// <summary>The working-tree version most tests use: line 2 → "two", line 15 → "fifteen".</summary>
    private static readonly string Changed = File20(new() { [2] = "two", [15] = "fifteen" });

    private static string Index(TempGitRepo repo, string path) => repo.Run("show", ":" + path).StandardOutput;

    private static string WorkTree(TempGitRepo repo, string path) => File.ReadAllText(Path.Combine(repo.Path, path));

    private static async Task<FileDiff> UnstagedDiff(TempGitRepo repo, string path)
    {
        var change = (await repo.Client.GetStatusAsync()).Changes.Single(c => c.Path == path);
        return await repo.Client.GetDiffAsync(change, staged: false);
    }

    private static async Task<FileDiff> StagedDiff(TempGitRepo repo, string path)
    {
        var change = (await repo.Client.GetStatusAsync()).Changes.Single(c => c.Path == path);
        return await repo.Client.GetDiffAsync(change, staged: true);
    }

    /// <summary>Select every line of hunk <paramref name="hunk"/>.</summary>
    private static Func<int, int, bool> Hunk(int hunk) => (h, _) => h == hunk;

    /// <summary>Select the lines of this kind with exactly this text.</summary>
    private static Func<int, int, bool> LinesOf(FileDiff diff, DiffLineKind kind, params string[] texts) =>
        (h, i) => diff.Hunks[h].Lines[i] is var l && l.Kind == kind && texts.Contains(l.Text);

    private static TempGitRepo RepoWithTwoChanges()
    {
        var repo = new TempGitRepo();
        repo.WriteFile("f.txt", Original);
        repo.CommitAll("original");
        repo.WriteFile("f.txt", Changed);
        return repo;
    }

    [GitFact]
    public async Task Staging_one_hunk_leaves_the_other_unstaged()
    {
        using var repo = RepoWithTwoChanges();
        var diff = await UnstagedDiff(repo, "f.txt");
        Assert.Equal(2, diff.Hunks.Count);

        Assert.True(await repo.Client.ApplyPartialAsync(diff, Hunk(1), PartialAction.Stage));

        Assert.Equal(File20(new() { [15] = "fifteen" }), Index(repo, "f.txt"));
        Assert.Equal(Changed, WorkTree(repo, "f.txt")); // the working tree is never touched by staging
    }

    [GitFact]
    public async Task Staging_only_the_added_line_of_a_change_keeps_the_old_line_too()
    {
        using var repo = RepoWithTwoChanges();
        var diff = await UnstagedDiff(repo, "f.txt");

        await repo.Client.ApplyPartialAsync(diff, LinesOf(diff, DiffLineKind.Added, "two"), PartialAction.Stage);

        // "-2" wasn't selected, so line 2 stays and "two" comes after it. Hunk 2 isn't staged.
        Assert.Equal(File20(insertAfter: new() { [2] = ["two"] }), Index(repo, "f.txt"));
    }

    [GitFact]
    public async Task Staging_only_the_removed_line_drops_it_without_adding_the_new_one()
    {
        using var repo = RepoWithTwoChanges();
        var diff = await UnstagedDiff(repo, "f.txt");

        await repo.Client.ApplyPartialAsync(diff, LinesOf(diff, DiffLineKind.Removed, "15"), PartialAction.Stage);

        Assert.Equal(File20(new() { [15] = null }), Index(repo, "f.txt"));
    }

    [GitFact]
    public async Task The_second_hunks_position_follows_what_the_first_one_changed()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("f.txt", Original);
        repo.CommitAll("original");
        // Hunk 1 adds two lines after line 1, hunk 2 changes line 18: when only one of the added
        // lines goes along, hunk 2's new start must move by one, not two.
        repo.WriteFile("f.txt", File20(new() { [18] = "eighteen" }, new() { [1] = ["1a", "1b"] }));
        var diff = await UnstagedDiff(repo, "f.txt");
        Assert.Equal(2, diff.Hunks.Count);

        await repo.Client.ApplyPartialAsync(diff, (h, i) => diff.Hunks[h].Lines[i].Text != "1b", PartialAction.Stage);

        Assert.Equal(File20(new() { [18] = "eighteen" }, new() { [1] = ["1a"] }), Index(repo, "f.txt"));
    }

    [GitFact]
    public async Task Unstaging_one_hunk_takes_only_that_change_out_of_the_index()
    {
        using var repo = RepoWithTwoChanges();
        repo.Run("add", "f.txt");
        var diff = await StagedDiff(repo, "f.txt");

        await repo.Client.ApplyPartialAsync(diff, Hunk(0), PartialAction.Unstage);

        Assert.Equal(File20(new() { [15] = "fifteen" }), Index(repo, "f.txt"));
        Assert.Equal(Changed, WorkTree(repo, "f.txt"));
    }

    [GitFact]
    public async Task Unstaging_a_single_added_line()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("f.txt", Original);
        repo.CommitAll("original");
        repo.WriteFile("f.txt", File20(insertAfter: new() { [1] = ["a", "b"] }));
        repo.Run("add", "f.txt");
        var diff = await StagedDiff(repo, "f.txt");

        await repo.Client.ApplyPartialAsync(diff, LinesOf(diff, DiffLineKind.Added, "a"), PartialAction.Unstage);

        Assert.Equal(File20(insertAfter: new() { [1] = ["b"] }), Index(repo, "f.txt"));
    }

    [GitFact]
    public async Task Discarding_one_hunk_restores_only_those_lines_in_the_working_tree()
    {
        using var repo = RepoWithTwoChanges();
        var diff = await UnstagedDiff(repo, "f.txt");

        await repo.Client.ApplyPartialAsync(diff, Hunk(0), PartialAction.Discard);

        Assert.Equal(File20(new() { [15] = "fifteen" }), WorkTree(repo, "f.txt").Replace("\r\n", "\n"));
        Assert.Equal(Original, Index(repo, "f.txt")); // nothing staged
    }

    [GitFact]
    public async Task Part_of_an_untracked_file_can_be_staged()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("keep.txt", "x\n");
        repo.CommitAll("first");
        repo.WriteFile("new.txt", Lines("one", "two", "three"));
        var diff = await UnstagedDiff(repo, "new.txt");

        await repo.Client.ApplyPartialAsync(diff, LinesOf(diff, DiffLineKind.Added, "one", "three"), PartialAction.Stage);

        Assert.Equal(Lines("one", "three"), Index(repo, "new.txt"));
        var file = (await repo.Client.GetStatusAsync()).Changes.Single(c => c.Path == "new.txt");
        Assert.True(file.IsStaged);   // added to the index…
        Assert.True(file.IsUnstaged); // …with "two" still only in the working tree
    }

    [GitFact]
    public async Task Unstaging_part_of_a_newly_added_file_keeps_the_file_staged()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("keep.txt", "x\n");
        repo.CommitAll("first");
        repo.WriteFile("new.txt", Lines("one", "two", "three"));
        repo.Run("add", "new.txt");
        var diff = await StagedDiff(repo, "new.txt");

        await repo.Client.ApplyPartialAsync(diff, LinesOf(diff, DiffLineKind.Added, "two"), PartialAction.Unstage);

        Assert.Equal(Lines("one", "three"), Index(repo, "new.txt"));
    }

    [GitFact]
    public async Task Crlf_files_keep_their_line_endings()
    {
        using var repo = new TempGitRepo();
        repo.Run("config", "core.autocrlf", "false");
        repo.WriteFile("w.txt", File20(newline: "\r\n"));
        repo.CommitAll("crlf");
        repo.WriteFile("w.txt", File20(new() { [2] = "two", [15] = "fifteen" }, newline: "\r\n"));
        var diff = await UnstagedDiff(repo, "w.txt");

        await repo.Client.ApplyPartialAsync(diff, Hunk(0), PartialAction.Stage);

        Assert.Equal(File20(new() { [2] = "two" }, newline: "\r\n"), Index(repo, "w.txt"));
    }

    [GitFact]
    public async Task A_last_line_without_newline_survives()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("n.txt", File20(finalNewline: false));
        repo.CommitAll("no eol");
        repo.WriteFile("n.txt", File20(new() { [2] = "two", [20] = "twenty" }, finalNewline: false));
        var diff = await UnstagedDiff(repo, "n.txt");

        await repo.Client.ApplyPartialAsync(diff, Hunk(1), PartialAction.Stage);

        Assert.Equal(File20(new() { [20] = "twenty" }, finalNewline: false), Index(repo, "n.txt"));
    }

    [Fact]
    public void Nothing_selected_gives_no_patch_and_unsupported_diffs_are_refused()
    {
        var diff = new FileDiff
        {
            OldPath = "f", NewPath = "f",
            Hunks = [new DiffHunk(1, 1, 1, 1, "", [new DiffLine(DiffLineKind.Removed, "a", 1, null), new DiffLine(DiffLineKind.Added, "b", null, 1)])],
        };

        Assert.Null(PatchBuilder.Build(diff, (_, _) => false, PartialAction.Stage));
        Assert.True(PatchBuilder.SelectsEverything(diff, (_, _) => true));
        Assert.False(PatchBuilder.SelectsEverything(diff, (_, i) => i == 0));
        Assert.Throws<NotSupportedException>(() => PatchBuilder.Build(new FileDiff { NewPath = "b", IsBinary = true }, (_, _) => true, PartialAction.Stage));
        Assert.Throws<NotSupportedException>(() => PatchBuilder.Build(new FileDiff { OldPath = "a", NewPath = "b" }, (_, _) => true, PartialAction.Stage));
    }

    [Fact]
    public void Patch_text_for_a_single_line()
    {
        var diff = new FileDiff
        {
            OldPath = "f.txt", NewPath = "f.txt",
            Hunks = [new DiffHunk(4, 3, 4, 3, "", [
                new DiffLine(DiffLineKind.Context, "x", 4, 4),
                new DiffLine(DiffLineKind.Removed, "old", 5, null),
                new DiffLine(DiffLineKind.Added, "new", null, 5),
                new DiffLine(DiffLineKind.Context, "y", 6, 6),
            ])],
        };

        var patch = PatchBuilder.Build(diff, (_, i) => i == 2, PartialAction.Stage);

        Assert.Equal(
            "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -4,3 +4,4 @@\n x\n old\n+new\n y\n",
            patch);
    }
}
