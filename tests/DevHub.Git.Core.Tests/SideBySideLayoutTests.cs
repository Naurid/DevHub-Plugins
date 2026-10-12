using DevHub.Git.Core;
using Xunit;

namespace DevHub.Git.Core.Tests;

public class SideBySideLayoutTests
{
    /// <summary>The samples are multi-line literals; git's output uses "\n" (see DiffParserTests).</summary>
    private static FileDiff Parse(string hunks) =>
        Assert.Single(DiffParser.Parse(("""
            diff --git a/f.txt b/f.txt
            index 1111111..2222222 100644
            --- a/f.txt
            +++ b/f.txt

            """ + hunks).ReplaceLineEndings("\n")));

    /// <summary>Each row as "left | right" ("·" for a filler, "@@" for a hunk header).</summary>
    private static string[] Show(FileDiff diff) =>
        SideBySideLayout.Build(diff)
            .Select(r => r.IsHunkHeader ? "@@" : $"{r.Left?.Text ?? "·"} | {r.Right?.Text ?? "·"}")
            .ToArray();

    [Fact]
    public void Context_lines_are_on_both_sides()
    {
        var diff = Parse("""
            @@ -1,3 +1,3 @@
             a
            -b
            +B
             c

            """);

        Assert.Equal(["@@", "a | a", "b | B", "c | c"], Show(diff));
    }

    [Fact]
    public void Added_lines_get_fillers_on_the_left_and_removed_lines_on_the_right()
    {
        var diff = Parse("""
            @@ -1,3 +1,3 @@
             a
            +new
             b
            -gone
             c

            """);

        Assert.Equal(["@@", "a | a", "· | new", "b | b", "gone | ·", "c | c"], Show(diff));
    }

    [Fact]
    public void Removed_and_added_lines_are_paired_and_the_shorter_side_padded()
    {
        var diff = Parse("""
            @@ -1,4 +1,5 @@
             a
            -one
            -two
            +ONE
            +TWO
            +THREE
             z

            """);

        Assert.Equal(["@@", "a | a", "one | ONE", "two | TWO", "· | THREE", "z | z"], Show(diff));
    }

    [Fact]
    public void Each_change_block_in_a_hunk_is_paired_on_its_own()
    {
        var diff = Parse("""
            @@ -1,6 +1,5 @@
            -x1
            -x2
            -x3
            +y1
             mid
            -p
            +q1
            +q2

            """);

        Assert.Equal(["@@", "x1 | y1", "x2 | ·", "x3 | ·", "mid | mid", "p | q1", "· | q2"], Show(diff));
    }

    [Fact]
    public void Every_hunk_starts_with_its_header()
    {
        var diff = Parse("""
            @@ -1,2 +1,2 @@
            -a
            +A
             b
            @@ -10,2 +10,2 @@
             j
            -k
            +K

            """);

        var rows = SideBySideLayout.Build(diff);

        Assert.Equal(["@@", "a | A", "b | b", "@@", "j | j", "k | K"], Show(diff));
        Assert.Same(diff.Hunks[1], rows[3].Hunk);
        Assert.All(rows.Skip(3), r => Assert.Equal(1, r.HunkIndex));
    }

    [Fact]
    public void Rows_keep_line_numbers_and_their_index_in_the_hunk()
    {
        var diff = Parse("""
            @@ -5,3 +5,3 @@
             e
            -f
            +F
             g

            """);

        var changed = SideBySideLayout.Build(diff)[2];

        Assert.Equal((6, null), (changed.Left!.OldLineNumber, changed.Left.NewLineNumber));
        Assert.Equal((null, 6), (changed.Right!.OldLineNumber, changed.Right.NewLineNumber));
        Assert.Equal((1, 2), (changed.LeftIndex, changed.RightIndex));
        var context = SideBySideLayout.Build(diff)[1];
        Assert.Equal((0, 0), (context.LeftIndex, context.RightIndex));
    }

    [Fact]
    public void A_diff_without_hunks_has_no_rows() =>
        Assert.Empty(SideBySideLayout.Build(new FileDiff { IsBinary = true }));
}
