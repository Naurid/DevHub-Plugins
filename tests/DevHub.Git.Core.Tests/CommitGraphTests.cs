using DevHub.Git.Core;
using Xunit;
using static DevHub.Git.Core.GraphEdgePart;

namespace DevHub.Git.Core.Tests;

public class CommitGraphTests
{
    private static GitCommit C(string id, params string[] parents) => new() { Id = id, ParentIds = parents };

    /// <summary>The edges of a row without colors, sorted, for comparing.</summary>
    private static string Shape(GraphRow row) =>
        string.Join(" ", row.Edges
            .OrderBy(e => e.Part).ThenBy(e => e.From).ThenBy(e => e.To)
            .Select(e => $"{(e.Part == Top ? "T" : "B")}{e.From}{e.To}"));

    [Fact]
    public void Linear_history_stays_in_one_lane()
    {
        var rows = CommitGraph.Layout([C("c", "b"), C("b", "a"), C("a")]);

        Assert.All(rows, r => Assert.Equal(0, r.Lane));
        Assert.Equal("B00", Shape(rows[0]));     // tip: line only downwards
        Assert.Equal("T00 B00", Shape(rows[1]));
        Assert.Equal("T00", Shape(rows[2]));     // root: line only from above
        Assert.All(rows, r => Assert.Equal(1, r.LaneCount));
    }

    [Fact]
    public void A_merge_opens_a_lane_for_its_second_parent_which_rejoins_at_the_common_ancestor()
    {
        // m merges b (feature) into a (main); both come from root c.
        var rows = CommitGraph.Layout([C("m", "a", "b"), C("a", "c"), C("b", "c"), C("c")]);

        Assert.Equal(0, rows[0].Lane);
        Assert.Equal("B00 B01", Shape(rows[0]));           // straight down to a, bend out to b

        Assert.Equal(0, rows[1].Lane);                      // a: main line
        Assert.Equal("T00 T11 B00 B11", Shape(rows[1]));   // feature line passes by

        Assert.Equal(1, rows[2].Lane);                      // b: in the feature lane
        Assert.Equal("T00 T11 B00 B11", Shape(rows[2]));

        Assert.Equal(0, rows[3].Lane);                      // c: both lines end here
        Assert.Equal("T00 T10", Shape(rows[3]));
        Assert.Equal(2, rows[3].LaneCount);
    }

    [Fact]
    public void Each_line_keeps_its_color_and_new_lines_get_new_colors()
    {
        var rows = CommitGraph.Layout([C("m", "a", "b"), C("a", "c"), C("b", "c"), C("c")]);

        var main = rows[0].Color;
        var feature = rows[2].Color;
        Assert.NotEqual(main, feature);
        Assert.Equal(main, rows[1].Color);
        Assert.Equal(main, rows[3].Color);
        Assert.Contains(rows[0].Edges, e => e is { Part: Bottom, From: 0, To: 1 } && e.Color == feature);
        Assert.Contains(rows[3].Edges, e => e is { Part: Top, From: 1, To: 0 } && e.Color == feature);
    }

    [Fact]
    public void Two_unmerged_branch_tips_get_two_lanes()
    {
        // x and y both branch off c; nothing merges them.
        var rows = CommitGraph.Layout([C("x", "c"), C("y", "c"), C("c")]);

        Assert.Equal(0, rows[0].Lane);
        Assert.Equal("B00", Shape(rows[0]));
        Assert.Equal(1, rows[1].Lane);                // a new tip: first free lane, no line from above
        Assert.Equal("T00 B00 B11", Shape(rows[1]));
        Assert.Equal(0, rows[2].Lane);
        Assert.Equal("T00 T10", Shape(rows[2]));
    }

    [Fact]
    public void A_freed_lane_is_reused_and_the_graph_narrows_again()
    {
        // Feature merged early (m1), later a second feature (m2) reuses lane 1.
        var rows = CommitGraph.Layout([
            C("m2", "d", "f2"), C("d", "m1"), C("f2", "m1"),
            C("m1", "a", "f1"), C("a", "r"), C("f1", "r"), C("r"),
        ]);

        Assert.Equal(1, rows[2].Lane); // f2
        Assert.Equal(1, rows[5].Lane); // f1 reuses lane 1
        // m1: d's and f2's lines both end here, and f1's line bends out into the freed lane 1.
        Assert.Equal("T00 T10 B00 B01", Shape(rows[3]));
        Assert.Equal(2, rows.Max(r => r.LaneCount));
    }

    [Fact]
    public void An_octopus_merge_opens_one_lane_per_extra_parent()
    {
        var rows = CommitGraph.Layout([C("o", "a", "b", "c"), C("a", "r"), C("b", "r"), C("c", "r"), C("r")]);

        Assert.Equal("B00 B01 B02", Shape(rows[0]));
        Assert.Equal(new[] { 0, 0, 1, 2, 0 }, rows.Select(r => r.Lane));
        Assert.Equal("T00 T10 T20", Shape(rows[4]));
    }

    [Fact]
    public void A_parent_outside_the_loaded_page_leaves_the_line_open_at_the_bottom()
    {
        var rows = CommitGraph.Layout([C("b", "a")]); // a not loaded (only the newest commits)

        Assert.Equal("B00", Shape(rows[0]));
    }
}
