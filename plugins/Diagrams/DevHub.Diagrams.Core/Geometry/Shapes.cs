namespace DevHub.Diagrams.Core;

/// <summary>The outline of a shape, for where lines meet it and for hit tests.</summary>
public enum NodeShape
{
    Rectangle,
    RoundedRectangle,
    Ellipse,
    Diamond,
    /// <summary>A stick figure: lines meet its bounding box.</summary>
    Figure,
    /// <summary>A thin bar (fork / join): lines meet its long side.</summary>
    Bar,
    /// <summary>Points (a freehand stroke, a line shape).</summary>
    Path,
}

public static class Shapes
{
    /// <summary>
    /// Where a line from the shape's centre towards <paramref name="toward"/> leaves its outline. Lines are drawn
    /// between these points, so they touch an ellipse or a diamond rather than its bounding box.
    /// </summary>
    public static DPoint Boundary(NodeShape shape, DRect r, DPoint toward)
    {
        var c = r.Center;
        var d = toward - c;
        if (d.X == 0 && d.Y == 0)
            return c;
        var (a, b) = (r.Width / 2, r.Height / 2);
        switch (shape)
        {
            case NodeShape.Ellipse:
            {
                // Point on the ellipse x²/a² + y²/b² = 1 along d.
                var k = 1 / Math.Sqrt(d.X * d.X / (a * a) + d.Y * d.Y / (b * b));
                return c + d * k;
            }
            case NodeShape.Diamond:
            {
                // |x|/a + |y|/b = 1 along d.
                var k = 1 / (Math.Abs(d.X) / a + Math.Abs(d.Y) / b);
                return c + d * k;
            }
            default:
            {
                var kx = d.X == 0 ? double.PositiveInfinity : a / Math.Abs(d.X);
                var ky = d.Y == 0 ? double.PositiveInfinity : b / Math.Abs(d.Y);
                return c + d * Math.Min(kx, ky);
            }
        }
    }

    /// <summary>Whether a point is on a shape (with a few pixels of slack for thin ones).</summary>
    public static bool Hit(NodeShape shape, DRect r, DPoint p, double slack = 3)
    {
        var grown = r.Inflate(slack);
        if (!grown.Contains(p))
            return false;
        var c = r.Center;
        var (a, b) = (r.Width / 2 + slack, r.Height / 2 + slack);
        return shape switch
        {
            NodeShape.Ellipse => Math.Pow((p.X - c.X) / a, 2) + Math.Pow((p.Y - c.Y) / b, 2) <= 1,
            NodeShape.Diamond => Math.Abs(p.X - c.X) / a + Math.Abs(p.Y - c.Y) / b <= 1,
            _ => true,
        };
    }

    /// <summary>The distance from a point to a segment (hit tests on lines).</summary>
    public static double DistanceToSegment(DPoint p, DPoint a, DPoint b)
    {
        var ab = b - a;
        var len2 = ab.X * ab.X + ab.Y * ab.Y;
        if (len2 == 0)
            return p.DistanceTo(a);
        var t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>The distance from a point to a polyline.</summary>
    public static double DistanceToPath(DPoint p, IReadOnlyList<DPoint> path)
    {
        var best = double.PositiveInfinity;
        for (var i = 1; i < path.Count; i++)
            best = Math.Min(best, DistanceToSegment(p, path[i - 1], path[i]));
        return path.Count == 1 ? p.DistanceTo(path[0]) : best;
    }

    /// <summary>Whether a segment crosses a rectangle (routing avoids shapes with it).</summary>
    public static bool SegmentCrosses(DPoint a, DPoint b, DRect r)
    {
        if (r.Contains(a) || r.Contains(b))
            return true;
        // Liang–Barsky clipping.
        double t0 = 0, t1 = 1;
        var d = b - a;
        foreach (var (p, q) in new[] { (-d.X, a.X - r.Left), (d.X, r.Right - a.X), (-d.Y, a.Y - r.Top), (d.Y, r.Bottom - a.Y) })
        {
            if (p == 0)
            {
                if (q < 0)
                    return false;
                continue;
            }
            var t = q / p;
            if (p < 0)
                t0 = Math.Max(t0, t);
            else
                t1 = Math.Min(t1, t);
            if (t0 > t1)
                return false;
        }
        return true;
    }

    /// <summary>The point at a fraction of a polyline's length, and the direction there (labels, arrowheads).</summary>
    public static (DPoint Point, DPoint Direction) Along(IReadOnlyList<DPoint> path, double fraction)
    {
        if (path.Count == 0)
            return (default, new DPoint(1, 0));
        if (path.Count == 1)
            return (path[0], new DPoint(1, 0));
        var total = 0.0;
        for (var i = 1; i < path.Count; i++)
            total += path[i].DistanceTo(path[i - 1]);
        var target = total * Math.Clamp(fraction, 0, 1);
        for (var i = 1; i < path.Count; i++)
        {
            var len = path[i].DistanceTo(path[i - 1]);
            if (target <= len || i == path.Count - 1)
            {
                var dir = path[i] - path[i - 1];
                var t = len == 0 ? 0 : Math.Clamp(target / len, 0, 1);
                return (path[i - 1] + dir * t, len == 0 ? new DPoint(1, 0) : dir * (1 / len));
            }
            target -= len;
        }
        return (path[^1], new DPoint(1, 0));
    }
}
