using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Core;

/// <summary>How selected shapes line up (Align and Distribute in the toolbar).</summary>
public enum Alignment { Left, CenterX, Right, Top, CenterY, Bottom }

/// <summary>Snapping, alignment, distribution and finding what's under the pointer.</summary>
public static class Layout
{
    /// <summary>The nearest grid line (grid 0 = no snapping).</summary>
    public static double Snap(double value, double grid) => grid <= 0 ? value : Math.Round(value / grid) * grid;

    public static DPoint Snap(DPoint p, double grid) => new(Snap(p.X, grid), Snap(p.Y, grid));

    /// <summary>
    /// Smart guides: snaps a moving rectangle's edges or centre to the edges or centres of the others when one is
    /// within <paramref name="tolerance"/>; returns the correction and the guide lines to draw (x and y positions).
    /// </summary>
    public static (double Dx, double Dy, double? GuideX, double? GuideY) Guides(DRect moving, IEnumerable<DRect> others, double tolerance = 5)
    {
        double? bestX = null, bestY = null, guideX = null, guideY = null;
        var xs = new[] { moving.Left, moving.Center.X, moving.Right };
        var ys = new[] { moving.Top, moving.Center.Y, moving.Bottom };
        foreach (var o in others)
        {
            foreach (var target in new[] { o.Left, o.Center.X, o.Right })
                foreach (var x in xs)
                    if (Math.Abs(target - x) <= tolerance && (bestX is null || Math.Abs(target - x) < Math.Abs(bestX.Value)))
                        (bestX, guideX) = (target - x, target);
            foreach (var target in new[] { o.Top, o.Center.Y, o.Bottom })
                foreach (var y in ys)
                    if (Math.Abs(target - y) <= tolerance && (bestY is null || Math.Abs(target - y) < Math.Abs(bestY.Value)))
                        (bestY, guideY) = (target - y, target);
        }
        return (bestX ?? 0, bestY ?? 0, guideX, guideY);
    }

    /// <summary>The moves that line the shapes up; one step for Undo.</summary>
    public static IDiagramOperation Align(Diagram diagram, IReadOnlyList<string> ids, Alignment how)
    {
        var nodes = ids.Select(diagram.Node).OfType<DiagramNode>().ToList();
        var all = DRect.Around(nodes.Select(n => n.Bounds));
        var moves = new List<IDiagramOperation>();
        foreach (var n in nodes)
        {
            var (dx, dy) = how switch
            {
                Alignment.Left => (all.Left - n.X, 0.0),
                Alignment.CenterX => (all.Center.X - n.Center.X, 0),
                Alignment.Right => (all.Right - (n.X + n.Width), 0),
                Alignment.Top => (0, all.Top - n.Y),
                Alignment.CenterY => (0, all.Center.Y - n.Center.Y),
                _ => (0, all.Bottom - (n.Y + n.Height)),
            };
            if (dx != 0 || dy != 0)
                moves.Add(new MoveNodes([n.Id], dx, dy));
        }
        return new Batch(L.T("Align"), moves);
    }

    /// <summary>Spaces the shapes evenly between the first and the last (left to right, or top to bottom).</summary>
    public static IDiagramOperation Distribute(Diagram diagram, IReadOnlyList<string> ids, bool horizontally)
    {
        var nodes = ids.Select(diagram.Node).OfType<DiagramNode>()
            .OrderBy(n => horizontally ? n.X : n.Y).ToList();
        var moves = new List<IDiagramOperation>();
        if (nodes.Count < 3)
            return new Batch(L.T("Distribute"), moves);
        var first = nodes[0];
        var last = nodes[^1];
        var span = horizontally ? last.X + last.Width - first.X : last.Y + last.Height - first.Y;
        var sizes = nodes.Sum(n => horizontally ? n.Width : n.Height);
        var gap = (span - sizes) / (nodes.Count - 1);
        var at = horizontally ? first.X : first.Y;
        foreach (var n in nodes)
        {
            var delta = at - (horizontally ? n.X : n.Y);
            if (Math.Abs(delta) > 0.01)
                moves.Add(horizontally ? new MoveNodes([n.Id], delta, 0) : new MoveNodes([n.Id], 0, delta));
            at += (horizontally ? n.Width : n.Height) + gap;
        }
        return new Batch(L.T("Distribute"), moves);
    }

    /// <summary>The topmost node under a point (children before their containers), or null.</summary>
    public static DiagramNode? NodeAt(Diagram diagram, DPoint p, Func<DiagramNode, NodeShape> shapeOf, Func<DiagramNode, bool>? skip = null)
    {
        for (var i = diagram.Nodes.Count - 1; i >= 0; i--)
        {
            var n = diagram.Nodes[i];
            if (skip?.Invoke(n) == true)
                continue;
            var shape = shapeOf(n);
            if (shape == NodeShape.Path)
            {
                if (n.Points is { Count: > 0 } pts
                    && Shapes.DistanceToPath(p, [.. pts.Select(q => new DPoint(n.X + q.X, n.Y + q.Y))]) <= 5)
                    return n;
                continue;
            }
            if (Shapes.Hit(shape, n.Bounds, p))
                return n;
        }
        return null;
    }

    /// <summary>
    /// What a click at <paramref name="p"/> lands on, in the order the diagram is drawn: a shape, then a line, then the
    /// innermost container (a package, a boundary, a lane): lines inside a container are picked before the container.
    /// </summary>
    public static (DiagramNode? Node, DiagramEdge? Edge) HitAt(Diagram diagram, DPoint p, Func<DiagramNode, NodeShape> shapeOf,
        Func<DiagramNode, bool> isContainer, Func<DiagramEdge, IReadOnlyList<DPoint>> pathOf, double tolerance)
    {
        if (NodeAt(diagram, p, shapeOf, isContainer) is { } shape)
            return (shape, null);
        if (EdgeAt(diagram, p, pathOf, tolerance) is { } edge)
            return (null, edge);
        DiagramNode? inner = null;
        var innerDepth = -1;
        foreach (var n in diagram.Nodes.Where(isContainer))
        {
            if (!Shapes.Hit(shapeOf(n), n.Bounds, p))
                continue;
            var depth = 0;
            var seen = new HashSet<string>();
            for (var parent = n.ParentId; parent is not null && seen.Add(parent); parent = diagram.Node(parent)?.ParentId)
                depth++;
            if (depth >= innerDepth)
                (inner, innerDepth) = (n, depth);
        }
        return (inner, null);
    }

    /// <summary>The line nearest a point within <paramref name="tolerance"/>, or null.</summary>
    public static DiagramEdge? EdgeAt(Diagram diagram, DPoint p, Func<DiagramEdge, IReadOnlyList<DPoint>> pathOf, double tolerance = 5)
    {
        DiagramEdge? best = null;
        var bestDistance = tolerance;
        foreach (var e in diagram.Edges)
        {
            var d = Shapes.DistanceToPath(p, pathOf(e));
            if (d <= bestDistance)
                (best, bestDistance) = (e, d);
        }
        return best;
    }

    /// <summary>The container a shape dropped at a point lands in: the smallest container around it, or null.</summary>
    public static DiagramNode? ContainerFor(Diagram diagram, DRect bounds, Func<DiagramNode, bool> isContainer, IReadOnlyCollection<string> moving)
    {
        return diagram.Nodes
            .Where(n => isContainer(n) && !moving.Contains(n.Id) && n.Bounds.Contains(bounds.Center)
                        && !moving.Any(id => diagram.WithDescendants(id).Any(d => d.Id == n.Id)))
            .OrderBy(n => n.Width * n.Height)
            .FirstOrDefault();
    }
}
