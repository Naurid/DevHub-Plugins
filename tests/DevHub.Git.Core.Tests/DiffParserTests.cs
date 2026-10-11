using DevHub.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Git.Core.Tests;

public class DiffParserTests
{
    /// <summary>
    /// git's output uses "\n". The sample diffs below are multi-line literals, which take this
    /// file's own line endings, and those depend on how it was checked out (core.autocrlf turns
    /// them into "\r\n" on Windows). Normalise, so the tests don't depend on the checkout.
    /// </summary>
    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    // ---- Parser on fixed text -------------------------------------------------------------

    [Fact]
    public void Modified_file_with_two_hunks_gets_line_numbers()
    {
        const string output = """
            diff --git a/src/App.cs b/src/App.cs
            index 1111111..2222222 100644
            --- a/src/App.cs
            +++ b/src/App.cs
            @@ -1,3 +1,3 @@ namespace X
             one
            -two
            +TWO
             three
            @@ -10,2 +10,3 @@
             ten
            +ten and a half
             eleven

            """;

        var diff = Assert.Single(DiffParser.Parse(Lf(output)));

        Assert.Equal("src/App.cs", diff.OldPath);
        Assert.Equal("src/App.cs", diff.NewPath);
        Assert.False(diff.IsNewFile || diff.IsDeleted || diff.IsRenamed || diff.IsBinary);
        Assert.Equal(2, diff.Hunks.Count);
        Assert.Equal((2, 1), (diff.AddedCount, diff.RemovedCount));

        var first = diff.Hunks[0];
        Assert.Equal("namespace X", first.Section);
        Assert.Equal("@@ -1,3 +1,3 @@ namespace X", first.Header);
        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Context, "one", 1, 1),
                new DiffLine(DiffLineKind.Removed, "two", 2, null),
                new DiffLine(DiffLineKind.Added, "TWO", null, 2),
                new DiffLine(DiffLineKind.Context, "three", 3, 3),
            ],
            first.Lines);

        var second = diff.Hunks[1];
        Assert.Equal(new DiffLine(DiffLineKind.Added, "ten and a half", null, 11), second.Lines[1]);
        Assert.Equal(new DiffLine(DiffLineKind.Context, "eleven", 11, 12), second.Lines[2]);
    }

    [Fact]
    public void New_and_deleted_files()
    {
        const string output = """
            diff --git a/new.txt b/new.txt
            new file mode 100644
            index 0000000..3333333
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1,2 @@
            +a
            +b
            diff --git a/gone.txt b/gone.txt
            deleted file mode 100644
            index 4444444..0000000
            --- a/gone.txt
            +++ /dev/null
            @@ -1 +0,0 @@
            -bye

            """;

        var diffs = DiffParser.Parse(Lf(output));

        Assert.Equal(2, diffs.Count);
        Assert.True(diffs[0].IsNewFile);
        Assert.Null(diffs[0].OldPath);
        Assert.Equal("new.txt", diffs[0].Path);
        Assert.Equal(2, diffs[0].AddedCount);

        Assert.True(diffs[1].IsDeleted);
        Assert.Null(diffs[1].NewPath);
        Assert.Equal("gone.txt", diffs[1].Path);
        // "@@ -1 +0,0 @@": a missing count means 1.
        Assert.Equal(1, diffs[1].Hunks[0].OldCount);
        Assert.Equal(new DiffLine(DiffLineKind.Removed, "bye", 1, null), diffs[1].Hunks[0].Lines[0]);
    }

    [Fact]
    public void Pure_rename_has_paths_but_no_hunks()
    {
        const string output = """
            diff --git a/old name.txt b/new name.txt
            similarity index 100%
            rename from old name.txt
            rename to new name.txt

            """;

        var diff = Assert.Single(DiffParser.Parse(Lf(output)));

        Assert.True(diff.IsRenamed);
        Assert.Equal("old name.txt", diff.OldPath);
        Assert.Equal("new name.txt", diff.NewPath);
        Assert.Empty(diff.Hunks);
    }

    [Fact]
    public void Binary_file_is_flagged_and_gets_its_path_from_the_diff_line()
    {
        const string output = """
            diff --git a/img/logo.png b/img/logo.png
            index 5555555..6666666 100644
            Binary files a/img/logo.png and b/img/logo.png differ

            """;

        var diff = Assert.Single(DiffParser.Parse(Lf(output)));

        Assert.True(diff.IsBinary);
        Assert.Equal("img/logo.png", diff.Path);
        Assert.Empty(diff.Hunks);
    }

    [Fact]
    public void Mode_change_only()
    {
        const string output = """
            diff --git a/run.sh b/run.sh
            old mode 100644
            new mode 100755

            """;

        var diff = Assert.Single(DiffParser.Parse(Lf(output)));

        Assert.Equal("100644 → 100755", diff.ModeChange);
        Assert.Equal("run.sh", diff.Path);
    }

    [Fact]
    public void No_newline_marker_attaches_to_the_previous_line()
    {
        const string output = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1 +1 @@
            -old
            \ No newline at end of file
            +new
            \ No newline at end of file

            """;

        var lines = Assert.Single(DiffParser.Parse(Lf(output))).Hunks[0].Lines;

        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].NoNewlineAtEnd);
        Assert.True(lines[1].NoNewlineAtEnd);
        Assert.Equal("new", lines[1].Text);
    }

    [Fact]
    public void Crlf_is_stripped_but_remembered_and_lines_that_look_like_headers_stay_content()
    {
        // A removed line "-- x" shows as "--- x"; inside a hunk it's content, not a header.
        var output = "diff --git a/a.txt b/a.txt\r\n--- a/a.txt\r\n+++ b/a.txt\r\n@@ -1,2 +1,1 @@\r\n--- x\r\n context\r\n";

        var diff = Assert.Single(DiffParser.Parse(output));

        Assert.Equal("a.txt", diff.Path);
        Assert.Equal(
            [new DiffLine(DiffLineKind.Removed, "-- x", 1, null) { HasCarriageReturn = true },
             new DiffLine(DiffLineKind.Context, "context", 2, 1) { HasCarriageReturn = true }],
            diff.Hunks[0].Lines);
    }

    [Fact]
    public void Quoted_paths_are_unquoted()
    {
        Assert.Equal("tab\there.txt", DiffParser.Unquote("\"tab\\there.txt\""));
        Assert.Equal("say \"hi\".txt", DiffParser.Unquote("\"say \\\"hi\\\".txt\""));
        Assert.Equal("café.txt", DiffParser.Unquote("\"caf\\303\\251.txt\""));
        Assert.Equal("plain.txt", DiffParser.Unquote("plain.txt"));
    }

    [Fact]
    public void Combined_diff_of_a_conflict()
    {
        const string output = """
            diff --cc f.txt
            index 1111111,2222222..0000000
            --- a/f.txt
            +++ b/f.txt
            @@@ -1,1 -1,1 +1,5 @@@
            ++<<<<<<< HEAD
             +ours
            ++=======
            + theirs
            ++>>>>>>> other

            """;

        var diff = Assert.Single(DiffParser.Parse(Lf(output)));

        Assert.True(diff.IsCombined);
        Assert.Equal("f.txt", diff.Path);
        var lines = diff.Hunks[0].Lines;
        Assert.Equal(5, lines.Count);
        Assert.All(lines, l => Assert.Equal(DiffLineKind.Added, l.Kind));
        Assert.Equal("<<<<<<< HEAD", lines[0].Text);
        Assert.Equal([1, 2, 3, 4, 5], lines.Select(l => l.NewLineNumber!.Value));
    }

    [Fact]
    public void Long_diffs_are_cut_off_at_the_limit()
    {
        var output = "diff --git a/big.txt b/big.txt\n--- a/big.txt\n+++ b/big.txt\n@@ -0,0 +1,100 @@\n"
                     + string.Concat(Enumerable.Range(1, 100).Select(i => $"+line {i}\n"));

        var diff = Assert.Single(DiffParser.Parse(Lf(output), maxLinesPerFile: 10));

        Assert.True(diff.IsTruncated);
        Assert.Equal(10, diff.Hunks[0].Lines.Count);
    }

    [Theory]
    // Added after two context lines → line 3.
    [InlineData("@@ -1,3 +1,4 @@\n a\n b\n+new\n c\n", 3)]
    // Removed after one context line → the line that's now where it was (2).
    [InlineData("@@ -1,3 +1,2 @@\n a\n-gone\n c\n", 2)]
    // A whole file deleted ("+0,0") → line 1, never 0.
    [InlineData("@@ -1,2 +0,0 @@\n-a\n-b\n", 1)]
    // Change deep in the file.
    [InlineData("@@ -40,3 +40,3 @@\n x\n-y\n+Y\n z\n", 41)]
    public void First_changed_line_is_where_to_open_the_file(string hunks, int expected)
    {
        var output = "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n" + hunks;

        Assert.Equal(expected, Assert.Single(DiffParser.Parse(Lf(output))).FirstChangedLine);
    }

    [Fact]
    public void Empty_output_gives_no_files()
    {
        Assert.Empty(DiffParser.Parse(""));
    }

    // ---- Against real git -----------------------------------------------------------------

    [GitFact]
    public async Task Unstaged_and_staged_sides_of_the_same_file()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "one\ntwo\n");
        repo.CommitAll("first");
        repo.WriteFile("a.txt", "one\nTWO\n");
        repo.Run("add", "a.txt");
        repo.WriteFile("a.txt", "one\nTWO\nthree\n");

        var change = Assert.Single((await repo.Client.GetStatusAsync()).Changes);
        var staged = await repo.Client.GetDiffAsync(change, staged: true);
        var unstaged = await repo.Client.GetDiffAsync(change, staged: false);

        Assert.Equal(["two", "TWO"], staged.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind != DiffLineKind.Context).Select(l => l.Text));
        Assert.Equal(["three"], unstaged.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == DiffLineKind.Added).Select(l => l.Text));
        Assert.Equal(0, unstaged.RemovedCount);
    }

    [GitFact]
    public async Task Untracked_file_shows_as_all_new()
    {
        using var repo = new TempGitRepo();
        // src/ must already be tracked, or status lists the whole new folder as "src/".
        repo.WriteFile("src/keep.txt", "x\n");
        repo.CommitAll("first");
        repo.WriteFile("src/new file.cs", "class A {}\nclass B {}\n");

        var change = Assert.Single((await repo.Client.GetStatusAsync()).Changes);
        var diff = await repo.Client.GetDiffAsync(change, staged: false);

        Assert.True(diff.IsNewFile);
        Assert.Equal("src/new file.cs", diff.Path);
        Assert.Equal(["class A {}", "class B {}"], diff.Hunks.Single().Lines.Select(l => l.Text));
        Assert.Equal(2, diff.AddedCount);
    }

    [GitFact]
    public async Task Untracked_folder_is_not_diffed()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("keep.txt", "x\n");
        repo.CommitAll("first");
        repo.WriteFile("newdir/a.txt", "a\n");

        var change = Assert.Single((await repo.Client.GetStatusAsync()).Changes);
        var diff = await repo.Client.GetDiffAsync(change, staged: false);

        Assert.True(diff.IsDirectory);
        Assert.Equal("newdir/", diff.Path);
    }

    [GitFact]
    public async Task Staged_rename_with_an_edit_is_one_file_diff()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("old.txt", "1\n2\n3\n4\n5\n6\n7\n8\n");
        repo.CommitAll("first");
        repo.Run("mv", "old.txt", "new.txt");
        repo.WriteFile("new.txt", "1\n2\n3\n4\n5\n6\n7\neight\n");
        repo.Run("add", "new.txt");

        var change = Assert.Single((await repo.Client.GetStatusAsync()).Changes);
        var diff = await repo.Client.GetDiffAsync(change, staged: true);

        Assert.True(diff.IsRenamed);
        Assert.Equal("old.txt", diff.OldPath);
        Assert.Equal("new.txt", diff.NewPath);
        Assert.Equal((1, 1), (diff.AddedCount, diff.RemovedCount));
    }

    [GitFact]
    public async Task Binary_file_is_detected()
    {
        using var repo = new TempGitRepo();
        File.WriteAllBytes(Path.Combine(repo.Path, "b.bin"), [0, 1, 2, 0, 255]);
        repo.CommitAll("first");
        File.WriteAllBytes(Path.Combine(repo.Path, "b.bin"), [0, 1, 2, 0, 254, 0]);

        var change = Assert.Single((await repo.Client.GetStatusAsync()).Changes);
        var diff = await repo.Client.GetDiffAsync(change, staged: false);

        Assert.True(diff.IsBinary);
        Assert.Equal("b.bin", diff.Path);
    }

    [GitFact]
    public async Task User_config_cannot_change_the_format()
    {
        using var repo = new TempGitRepo();
        repo.Run("config", "diff.noprefix", "true");
        repo.Run("config", "color.ui", "always");
        repo.WriteFile("a.txt", "1\n");
        repo.CommitAll("first");
        repo.WriteFile("a.txt", "2\n");

        var change = Assert.Single((await repo.Client.GetStatusAsync()).Changes);
        var diff = await repo.Client.GetDiffAsync(change, staged: false);

        Assert.Equal("a.txt", diff.Path);
        Assert.Equal(["1", "2"], diff.Hunks.Single().Lines.Select(l => l.Text));
    }

    [GitFact]
    public async Task Conflicted_file_gives_a_combined_diff()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("f.txt", "base\n");
        repo.CommitAll("base");
        repo.Run("checkout", "-b", "other");
        repo.WriteFile("f.txt", "theirs\n");
        repo.CommitAll("theirs");
        repo.Run("checkout", "main");
        repo.WriteFile("f.txt", "ours\n");
        repo.CommitAll("ours");
        await repo.Runner.RunAsync(repo.Path, ["merge", "other"], new GitRunOptions { AllowFailure = true });

        var change = Assert.Single((await repo.Client.GetStatusAsync()).Changes);
        var diff = await repo.Client.GetDiffAsync(change, staged: false);

        Assert.True(diff.IsCombined);
        Assert.Contains(diff.Hunks.SelectMany(h => h.Lines), l => l.Text.StartsWith("<<<<<<<", StringComparison.Ordinal));
    }
}
