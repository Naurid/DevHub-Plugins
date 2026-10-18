namespace DevHub.Diagrams.Core;

/// <summary>
/// Where lines run. A line goes from the outline of one shape to the outline of the other: straight (through any
/// waypoints the user set), or orthogonal with up to three segments, choosing the sides that cross the fewest other
/// shapes and then the shortest way.
/// </summary>
public static class Routing
{
    /// <summary>The points a line is drawn through, end to end.</summary>
    /// <param name="shapeOf">The outline of each node (the kind knows it).</param>
    public static IReadOnlyList<DPoint> Path(Diagram diagram, DiagramEdge edge, Func<DiagramNode, NodeShape> shapeOf)
    {
        var from = diagram.Node(edge.From);
        var to = diagram.Node(edge.To);
        if (from is null || to is null)
            return [];
        if (from == to)
            return SelfLoop(from.Bounds);

        if (edge.Routing == EdgeRouting.Orthogonal && edge.Waypoints.Count == 0)
        {
            var obstacles = diagram.Nodes
                .Where(n => n != from && n != to && !IsContainerOf(diagram, n, from) && !IsContainerOf(diagram, n, to)
                            && shapeOf(n) != NodeShape.Path)
                .Select(n => n.Bounds)
                .ToList();
            return Orthogonal(from.Bounds, to.Bounds, obstacles);
        }

        var points = new List<DPoint>();
        var firstTarget = edge.Waypoints.Count > 0 ? edge.Waypoints[0] : to.Center;
        var lastSource = edge.Waypoints.Count > 0 ? edge.Waypoints[^1] : from.Center;
        points.Add(Shapes.Boundary(shapeOf(from), from.Bounds, firstTarget));
        points.AddRange(edge.Waypoints);
        points.Add(Shapes.Boundary(shapeOf(to), to.Bounds, lastSource));
        return points;
    }

    private static bool IsContainerOf(Diagram diagram, DiagramNode container, DiagramNode node)
    {
        for (var p = diagram.Node(node.ParentId); p is not null; p = diagram.Node(p.ParentId))
            if (p == container)
                return true;
        return false;
    }

    /// <summary>A loop on one shape (a reflexive association): out of the right side, back in at the top.</summary>
    public static IReadOnlyList<DPoint> SelfLoop(DRect r)
    {
        const double reach = 28;
        var start = new DPoint(r.Right, r.Center.Y);
        var end = new DPoint(r.Center.X, r.Top);
        return [start, new DPoint(r.Right + reach, r.Center.Y), new DPoint(r.Right + reach, r.Top - reach),
                new DPoint(r.Center.X, r.Top - reach), end];
    }

    private enum Side { Left, Right, Top, Bottom }

    private static DPoint Anchor(DRect r, Side s) => s switch
    {
        Side.Left => new(r.Left, r.Center.Y),
        Side.Right => new(r.Right, r.Center.Y),
        Side.Top => new(r.Center.X, r.Top),
        _ => new(r.Center.X, r.Bottom),
    };

    private static bool Horizontal(Side s) => s is Side.Left or Side.Right;

    /// <summary>An orthogonal route between two rectangles that avoids <paramref name="obstacles"/> when it can.</summary>
    public static IReadOnlyList<DPoint> Orthogonal(DRect from, DRect to, IReadOnlyList<DRect> obstacles)
    {
        const double gap = 16;
        List<DPoint>? best = null;
        var bestScore = double.PositiveInfinity;
        foreach (var sa in Enum.GetValues<Side>())
        {
            foreach (var sb in Enum.GetValues<Side>())
            {
                var a = Anchor(from, sa);
                var b = Anchor(to, sb);
                foreach (var route in Connect(a, sa, b, sb, gap, obstacles))
                {
                    // Don't leave a shape back through itself.
                    if (Crosses(route, from.Inflate(-1), skipFirst: true) || Crosses(route, to.Inflate(-1), skipLast: true))
                        continue;
                    var hits = obstacles.Count(o => Crosses(route, o.Inflate(4)));
                    var length = 0.0;
                    for (var i = 1; i < route.Count; i++)
                        length += route[i].DistanceTo(route[i - 1]);
                    var score = hits * 100_000 + length + route.Count * 20;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = route;
                    }
                }
            }
        }
        return best ?? [from.Center, to.Center];
    }

    private static bool Crosses(IReadOnlyList<DPoint> route, DRect r, bool skipFirst = false, bool skipLast = false)
    {
        for (var i = 1; i < route.Count; i++)
        {
            var (a, b) = (route[i - 1], route[i]);
            if (skipFirst && i == 1)
                a = a + (b - a) * 0.5;
            if (skipLast && i == route.Count - 1)
                b = a + (b - a) * 0.5;
            if (Shapes.SegmentCrosses(a, b, r))
                return true;
        }
        return false;
    }

    // Leaves each anchor straight out of its side, then joins with one or two elbows. The joining channel runs
    // halfway, or just past the side of an obstacle (so a route can go around it).
    private static IEnumerable<List<DPoint>> Connect(DPoint a, Side sa, DPoint b, Side sb, double gap, IReadOnlyList<DRect> obstacles)
    {
        var a1 = Out(a, sa, gap);
        var b1 = Out(b, sb, gap);
        List<DPoint> Route(params DPoint[] middle) => Simplify([a, a1, .. middle, b1, b]);

        if (Horizontal(sa) && Horizontal(sb))
        {
            foreach (var x in Channels((a1.X + b1.X) / 2, obstacles.SelectMany(o => new[] { o.Left - gap, o.Right + gap })))
                yield return Route(new DPoint(x, a1.Y), new DPoint(x, b1.Y));
            // Around: out, along a free row, back.
            foreach (var y in obstacles.SelectMany(o => new[] { o.Top - gap, o.Bottom + gap }))
                yield return Route(new DPoint(a1.X, y), new DPoint(b1.X, y));
        }
        else if (!Horizontal(sa) && !Horizontal(sb))
        {
            foreach (var y in Channels((a1.Y + b1.Y) / 2, obstacles.SelectMany(o => new[] { o.Top - gap, o.Bottom + gap })))
                yield return Route(new DPoint(a1.X, y), new DPoint(b1.X, y));
            foreach (var x in obstacles.SelectMany(o => new[] { o.Left - gap, o.Right + gap }))
                yield return Route(new DPoint(x, a1.Y), new DPoint(x, b1.Y));
        }
        else if (Horizontal(sa))
            yield return Route(new DPoint(b1.X, a1.Y));
        else
            yield return Route(new DPoint(a1.X, b1.Y));
    }

    private static IEnumerable<double> Channels(double middle, IEnumerable<double> around) => around.Prepend(middle).Distinct();

    private static DPoint Out(DPoint p, Side s, double gap) => s switch
    {
        Side.Left => p with { X = p.X - gap },
        Side.Right => p with { X = p.X + gap },
        Side.Top => p with { Y = p.Y - gap },
        _ => p with { Y = p.Y + gap },
    };

    /// <summary>Drops repeated points and points in the middle of a straight run.</summary>
    public static List<DPoint> Simplify(List<DPoint> points)
    {
        var result = new List<DPoint>();
        foreach (var p in points)
        {
            if (result.Count > 0 && result[^1].DistanceTo(p) < 0.01)
                continue;
            if (result.Count >= 2)
            {
                var (a, b) = (result[^2], result[^1]);
                var collinear = (Math.Abs(a.X - b.X) < 0.01 && Math.Abs(b.X - p.X) < 0.01)
                                || (Math.Abs(a.Y - b.Y) < 0.01 && Math.Abs(b.Y - p.Y) < 0.01);
                if (collinear)
                    result.RemoveAt(result.Count - 1);
            }
            result.Add(p);
        }
        return result;
    }

    /// <summary>Where a line's label sits: at <see cref="DiagramEdge.LabelAt"/>, or the middle of its longest segment.</summary>
    public static DPoint LabelPoint(IReadOnlyList<DPoint> path, double? at)
    {
        if (at is { } fraction)
            return Shapes.Along(path, fraction).Point;
        if (path.Count < 2)
            return path.Count == 1 ? path[0] : default;
        var longest = 1;
        for (var i = 2; i < path.Count; i++)
            if (path[i].DistanceTo(path[i - 1]) > path[longest].DistanceTo(path[longest - 1]))
                longest = i;
        return path[longest - 1] + (path[longest] - path[longest - 1]) * 0.5;
    }

    /// <summary>The detail keys of a label dragged off its line: its offset from <see cref="LabelPoint"/>.</summary>
    public const string LabelDx = "labelDx", LabelDy = "labelDy";

    /// <summary>A label's offset from its point on the line (0, 0 unless it was dragged off it).</summary>
    public static DPoint LabelOffset(DiagramEdge edge) =>
        new(double.TryParse(edge.Prop(LabelDx), System.Globalization.CultureInfo.InvariantCulture, out var dx) ? dx : 0,
            double.TryParse(edge.Prop(LabelDy), System.Globalization.CultureInfo.InvariantCulture, out var dy) ? dy : 0);

    /// <summary>Where a line's label is drawn: its point on the line plus where it was dragged to.</summary>
    public static DPoint LabelCenter(IReadOnlyList<DPoint> path, DiagramEdge edge) => LabelPoint(path, edge.LabelAt) + LabelOffset(edge);

    /// <summary>
    /// Where a label dropped at <paramref name="at"/> sits: the fraction of the line nearest to it, and its offset from
    /// there. Kept that way, the label follows the line when its shapes move.
    /// </summary>
    public static (double At, DPoint Offset) PlaceLabel(IReadOnlyList<DPoint> path, DPoint at)
    {
        if (path.Count < 2)
            return (0.5, path.Count == 1 ? at - path[0] : default);
        var total = 0.0;
        for (var i = 1; i < path.Count; i++)
            total += path[i].DistanceTo(path[i - 1]);
        var (best, bestAlong, walked) = (double.MaxValue, 0.0, 0.0);
        for (var i = 1; i < path.Count; i++)
        {
            var (a, b) = (path[i - 1], path[i]);
            var d = b - a;
            var len = d.Length;
            var t = len == 0 ? 0 : Math.Clamp(((at.X - a.X) * d.X + (at.Y - a.Y) * d.Y) / (len * len), 0, 1);
            var distance = at.DistanceTo(a + d * t);
            if (distance < best)
                (best, bestAlong) = (distance, walked + len * t);
            walked += len;
        }
        var fraction = total == 0 ? 0.5 : bestAlong / total;
        return (fraction, at - Shapes.Along(path, fraction).Point);
    }
}
