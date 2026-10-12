namespace DevHub.Git.Core;

/// <summary>Which half of a row an edge is drawn in.</summary>
public enum GraphEdgePart
{
    /// <summary>From the top of the row (lane <see cref="GraphEdge.From"/>) to the middle (lane <see cref="GraphEdge.To"/>).</summary>
    Top,

    /// <summary>From the middle of the row (lane From) to the bottom (lane To).</summary>
    Bottom,
}

/// <summary>
/// A line segment in one row of the graph. A straight lane passing a row is a Top and a Bottom
/// edge with From == To; a branch or merge line bends from one lane to another within a half row.
/// </summary>
/// <param name="Color">Color index (the view picks <c>Color % palette size</c>).</param>
public readonly record struct GraphEdge(int From, int To, int Color, GraphEdgePart Part);

/// <summary>The graph for one commit row: where its dot is and which lines cross the row.</summary>
/// <param name="Lane">Column of the commit's dot (0 = leftmost).</param>
/// <param name="Color">Color index of the commit's lane.</param>
/// <param name="LaneCount">Columns this row uses (for its width).</param>
public sealed record GraphRow(int Lane, int Color, IReadOnlyList<GraphEdge> Edges, int LaneCount);

/// <summary>
/// Lays out the commit graph like Fork / JetBrains: each commit gets a lane (column), lines
/// connect commits to their parents, and lanes are reused once a branch line ends.
/// </summary>
/// <remarks>
/// <para>Input must be in <c>--topo-order</c> (children before parents). Walking down the list
/// we keep <c>lanes</c>: for each column, the commit id the line in that column is heading for
/// (null = free). For each commit:</para>
/// <list type="number">
/// <item>Its lane is the leftmost column waiting for it; if none is (a branch tip), the first free one.</item>
/// <item>Every other column waiting for it ends here: a Top edge bends from that column into the
/// commit (branches converging on their common parent).</item>
/// <item>The first parent continues in the commit's own column. Each further parent (a merge)
/// bends into the column already waiting for it, or into a new one.</item>
/// <item>Columns not involved pass straight through.</item>
/// </list>
/// <para>Columns never move sideways, so lines stay straight; freed columns are reused from the
/// left. Each new line gets the next color, and keeps it all the way down.</para>
/// </remarks>
public static class CommitGraph
{
    public static IReadOnlyList<GraphRow> Layout(IReadOnlyList<GitCommit> commits)
    {
        var lanes = new List<string?>();
        var colors = new List<int>();
        var nextColor = 0;
        var rows = new List<GraphRow>(commits.Count);

        foreach (var commit in commits)
        {
            var edges = new List<GraphEdge>();

            // 1. The commit's column.
            var lane = lanes.IndexOf(commit.Id);
            var isTip = lane < 0;
            if (isTip)
            {
                lane = FreeLane(lanes);
                lanes[lane] = commit.Id;
                colors[lane] = nextColor++;
            }
            var color = colors[lane];

            // 2. Lines arriving from above.
            for (var j = 0; j < lanes.Count; j++)
            {
                if (lanes[j] is null)
                    continue;
                if (lanes[j] == commit.Id)
                {
                    if (!isTip || j != lane)
                        edges.Add(new GraphEdge(j, lane, colors[j], GraphEdgePart.Top));
                    if (j != lane)
                        lanes[j] = null; // that line ends in this commit
                }
                else
                {
                    edges.Add(new GraphEdge(j, j, colors[j], GraphEdgePart.Top)); // passes through
                }
            }

            // 3. Lines leaving downwards: first parent straight down, further parents bend out.
            lanes[lane] = commit.ParentIds.Count > 0 ? commit.ParentIds[0] : null;
            var opened = new HashSet<int>(); // columns a merge line starts in this row
            for (var p = 1; p < commit.ParentIds.Count; p++)
            {
                var parent = commit.ParentIds[p];
                var target = lanes.IndexOf(parent);
                if (target < 0)
                {
                    target = FreeLane(lanes);
                    lanes[target] = parent;
                    colors[target] = nextColor++;
                    opened.Add(target);
                }
                edges.Add(new GraphEdge(lane, target, colors[target], GraphEdgePart.Bottom));
            }

            // 4. Every column still holding a line continues to the next row, except columns a
            //    merge line only just bent into (their line starts at this commit's dot).
            for (var j = 0; j < lanes.Count; j++)
            {
                if (lanes[j] is not null && !opened.Contains(j))
                    edges.Add(new GraphEdge(j, j, colors[j], GraphEdgePart.Bottom));
            }

            var laneCount = Math.Max(lane + 1, edges.Count == 0 ? 0 : edges.Max(e => Math.Max(e.From, e.To)) + 1);
            rows.Add(new GraphRow(lane, color, edges, laneCount));

            // Free columns at the right end so the graph gets narrower again.
            while (lanes.Count > 0 && lanes[^1] is null)
            {
                lanes.RemoveAt(lanes.Count - 1);
                colors.RemoveAt(colors.Count - 1);
            }
        }

        return rows;

        int FreeLane(List<string?> l)
        {
            var free = l.IndexOf(null);
            if (free >= 0)
                return free;
            l.Add(null);
            colors.Add(0);
            return l.Count - 1;
        }
    }
}
