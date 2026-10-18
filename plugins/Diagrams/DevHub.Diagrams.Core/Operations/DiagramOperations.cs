using System.Text.Json;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Core;

/// <summary>
/// One change to a diagram as a plain object (<c>new MoveNodes(ids, 20, 0)</c>). <see cref="Apply"/> makes the
/// change and returns the operation that undoes it, so Undo / Redo is a stack of these (<see cref="UndoStack"/>).
/// The canvas, the properties panel, the converters and the tests all change diagrams this way.
/// </summary>
public interface IDiagramOperation
{
    /// <summary>What it does, for "Undo: Move" ("Move", "Delete", "Rename"…).</summary>
    string Name { get; }

    /// <summary>Makes the change and returns its undo.</summary>
    IDiagramOperation Apply(Diagram diagram);
}

internal static class Copy
{
    public static DiagramNode Of(DiagramNode n) => JsonSerializer.Deserialize<DiagramNode>(JsonSerializer.Serialize(n))!;
    public static DiagramEdge Of(DiagramEdge e) => JsonSerializer.Deserialize<DiagramEdge>(JsonSerializer.Serialize(e))!;
}

/// <summary>Adds shapes and lines (at given positions in the drawing order, or on top).</summary>
public sealed record AddElements(IReadOnlyList<DiagramNode> Nodes, IReadOnlyList<DiagramEdge> Edges, string? Label = null) : IDiagramOperation
{
    /// <summary>Where each node goes in the drawing order (by node id); missing = on top. Undo of a delete uses it.</summary>
    public IReadOnlyDictionary<string, int>? NodeIndexes { get; init; }

    public IReadOnlyDictionary<string, int>? EdgeIndexes { get; init; }

    public string Name => Label ?? L.T("Add");

    public IDiagramOperation Apply(Diagram diagram)
    {
        foreach (var n in Nodes.OrderBy(n => NodeIndexes?.GetValueOrDefault(n.Id, int.MaxValue) ?? int.MaxValue))
        {
            var copy = Copy.Of(n);
            var at = NodeIndexes is not null && NodeIndexes.TryGetValue(n.Id, out var i) ? Math.Min(i, diagram.Nodes.Count) : diagram.Nodes.Count;
            diagram.Nodes.Insert(at, copy);
        }
        foreach (var e in Edges.OrderBy(e => EdgeIndexes?.GetValueOrDefault(e.Id, int.MaxValue) ?? int.MaxValue))
        {
            var copy = Copy.Of(e);
            var at = EdgeIndexes is not null && EdgeIndexes.TryGetValue(e.Id, out var i) ? Math.Min(i, diagram.Edges.Count) : diagram.Edges.Count;
            diagram.Edges.Insert(at, copy);
        }
        return new RemoveElements([.. Nodes.Select(n => n.Id), .. Edges.Select(e => e.Id)], Name);
    }
}

/// <summary>Deletes shapes (with what's inside them and the lines touching them) and lines.</summary>
public sealed record RemoveElements(IReadOnlyList<string> Ids, string? Label = null) : IDiagramOperation
{
    public string Name => Label ?? L.T("Delete");

    public IDiagramOperation Apply(Diagram diagram)
    {
        var nodeIds = Ids.SelectMany(id => diagram.WithDescendants(id)).Select(n => n.Id).ToHashSet();
        var edgeIds = diagram.Edges.Where(e => Ids.Contains(e.Id) || nodeIds.Contains(e.From) || nodeIds.Contains(e.To))
            .Select(e => e.Id).ToHashSet();

        var nodeIndexes = new Dictionary<string, int>();
        var edgeIndexes = new Dictionary<string, int>();
        for (var i = 0; i < diagram.Nodes.Count; i++)
            if (nodeIds.Contains(diagram.Nodes[i].Id))
                nodeIndexes[diagram.Nodes[i].Id] = i;
        for (var i = 0; i < diagram.Edges.Count; i++)
            if (edgeIds.Contains(diagram.Edges[i].Id))
                edgeIndexes[diagram.Edges[i].Id] = i;

        var nodes = diagram.Nodes.Where(n => nodeIds.Contains(n.Id)).Select(Copy.Of).ToList();
        var edges = diagram.Edges.Where(e => edgeIds.Contains(e.Id)).Select(Copy.Of).ToList();
        diagram.Nodes.RemoveAll(n => nodeIds.Contains(n.Id));
        diagram.Edges.RemoveAll(e => edgeIds.Contains(e.Id));
        return new AddElements(nodes, edges, Name) { NodeIndexes = nodeIndexes, EdgeIndexes = edgeIndexes };
    }
}

/// <summary>Moves shapes (and what's inside them, and the bends of lines between moved shapes).</summary>
public sealed record MoveNodes(IReadOnlyList<string> Ids, double Dx, double Dy) : IDiagramOperation
{
    public string Name => L.T("Move");

    public IDiagramOperation Apply(Diagram diagram)
    {
        var moved = Ids.SelectMany(id => diagram.WithDescendants(id)).Select(n => n.Id).ToHashSet();
        foreach (var n in diagram.Nodes.Where(n => moved.Contains(n.Id)))
        {
            n.X += Dx;
            n.Y += Dy;
        }
        foreach (var e in diagram.Edges.Where(e => moved.Contains(e.From) && moved.Contains(e.To)))
            e.Waypoints = e.Waypoints.Select(p => new DPoint(p.X + Dx, p.Y + Dy)).ToList();
        return new MoveNodes(Ids, -Dx, -Dy);
    }
}

/// <summary>Sets a shape's position and size (resize, or a precise value from the properties panel).</summary>
public sealed record SetBounds(string Id, DRect Bounds) : IDiagramOperation
{
    public string Name => L.T("Resize");

    public IDiagramOperation Apply(Diagram diagram)
    {
        var n = diagram.Node(Id) ?? throw new DiagramRuleException(L.T("The shape no longer exists."));
        var old = n.Bounds;
        (n.X, n.Y, n.Width, n.Height) = (Bounds.X, Bounds.Y, Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height));
        return new SetBounds(Id, old);
    }
}

/// <summary>Turns a shape (degrees, clockwise).</summary>
public sealed record SetRotation(string Id, double Degrees) : IDiagramOperation
{
    public string Name => L.T("Rotate");

    public IDiagramOperation Apply(Diagram diagram)
    {
        var n = diagram.Node(Id) ?? throw new DiagramRuleException(L.T("The shape no longer exists."));
        var old = n.Rotation;
        n.Rotation = ((Degrees % 360) + 360) % 360;
        return new SetRotation(Id, old);
    }
}

/// <summary>Changes the text of a shape or a line.</summary>
public sealed record SetText(string Id, string Text) : IDiagramOperation
{
    public string Name => L.T("Edit Text");

    public IDiagramOperation Apply(Diagram diagram)
    {
        if (diagram.Node(Id) is { } n)
        {
            var old = n.Text;
            n.Text = Text;
            return new SetText(Id, old);
        }
        if (diagram.Edge(Id) is { } e)
        {
            var old = e.Text;
            e.Text = Text;
            return new SetText(Id, old);
        }
        throw new DiagramRuleException(L.T("The element no longer exists."));
    }
}

/// <summary>Sets details of a shape, a line or (Id null) the diagram; a null value removes the detail.</summary>
public sealed record SetProps(string? Id, IReadOnlyDictionary<string, string?> Values, string? Label = null) : IDiagramOperation
{
    public string Name => Label ?? L.T("Change");

    public IDiagramOperation Apply(Diagram diagram)
    {
        var props = Id is null ? diagram.Props
            : diagram.Node(Id)?.Props ?? diagram.Edge(Id)?.Props
            ?? throw new DiagramRuleException(L.T("The element no longer exists."));
        var old = new Dictionary<string, string?>();
        foreach (var (key, value) in Values)
        {
            old[key] = props.TryGetValue(key, out var was) ? was : null;
            if (value is null)
                props.Remove(key);
            else
                props[key] = value;
        }
        return new SetProps(Id, old, Name);
    }
}

/// <summary>Puts shapes into a container (or, null, onto the canvas).</summary>
public sealed record SetParent(IReadOnlyList<string> Ids, string? ParentId) : IDiagramOperation
{
    public string Name => L.T("Move Into");

    public IDiagramOperation Apply(Diagram diagram)
    {
        // A container can't go inside itself or its own children.
        if (ParentId is not null && Ids.Any(id => diagram.WithDescendants(id).Any(n => n.Id == ParentId)))
            throw new DiagramRuleException(L.T("A shape can't go inside itself."));
        var undo = new List<IDiagramOperation>();
        foreach (var group in Ids.Select(diagram.Node).OfType<DiagramNode>().GroupBy(n => n.ParentId))
        {
            undo.Add(new SetParent([.. group.Select(n => n.Id)], group.Key));
            foreach (var n in group)
                n.ParentId = ParentId;
        }
        return new Batch(Name, undo);
    }
}

/// <summary>Changes where a line starts and ends.</summary>
public sealed record Reconnect(string Id, string From, string To) : IDiagramOperation
{
    public string Name => L.T("Reconnect");

    public IDiagramOperation Apply(Diagram diagram)
    {
        var e = diagram.Edge(Id) ?? throw new DiagramRuleException(L.T("The line no longer exists."));
        if (diagram.Node(From) is null || diagram.Node(To) is null)
            throw new DiagramRuleException(L.T("A line must start and end on a shape."));
        var (from, to) = (e.From, e.To);
        (e.From, e.To) = (From, To);
        return new Reconnect(Id, from, to);
    }
}

/// <summary>Sets how a line runs: its bends, straight or orthogonal, where its label sits.</summary>
public sealed record SetEdgePath(string Id, IReadOnlyList<DPoint> Waypoints, EdgeRouting Routing, double? LabelAt) : IDiagramOperation
{
    public string Name => L.T("Change Line");

    public IDiagramOperation Apply(Diagram diagram)
    {
        var e = diagram.Edge(Id) ?? throw new DiagramRuleException(L.T("The line no longer exists."));
        var undo = new SetEdgePath(Id, [.. e.Waypoints], e.Routing, e.LabelAt);
        e.Waypoints = [.. Waypoints];
        e.Routing = Routing;
        e.LabelAt = LabelAt;
        return undo;
    }
}

/// <summary>Sets the points of a freehand stroke or a line shape (relative to the shape).</summary>
public sealed record SetPoints(string Id, IReadOnlyList<DPoint>? Points) : IDiagramOperation
{
    public string Name => L.T("Draw");

    public IDiagramOperation Apply(Diagram diagram)
    {
        var n = diagram.Node(Id) ?? throw new DiagramRuleException(L.T("The shape no longer exists."));
        var old = n.Points;
        n.Points = Points is null ? null : [.. Points];
        return new SetPoints(Id, old);
    }
}

/// <summary>Brings shapes to the front or sends them to the back.</summary>
public sealed record Reorder(IReadOnlyList<string> Ids, bool ToFront) : IDiagramOperation
{
    public string Name => ToFront ? L.T("Bring to Front") : L.T("Send to Back");

    public IDiagramOperation Apply(Diagram diagram)
    {
        var before = diagram.Nodes.Select(n => n.Id).ToList();
        var chosen = diagram.Nodes.Where(n => Ids.Contains(n.Id)).ToList();
        diagram.Nodes.RemoveAll(n => Ids.Contains(n.Id));
        if (ToFront)
            diagram.Nodes.AddRange(chosen);
        else
            diagram.Nodes.InsertRange(0, chosen);
        return new SetOrder(before, Name);
    }
}

/// <summary>Puts the shapes in a given drawing order (the undo of <see cref="Reorder"/>).</summary>
public sealed record SetOrder(IReadOnlyList<string> Order, string Label) : IDiagramOperation
{
    public string Name => Label;

    public IDiagramOperation Apply(Diagram diagram)
    {
        var before = diagram.Nodes.Select(n => n.Id).ToList();
        var rank = Order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        diagram.Nodes = [.. diagram.Nodes.OrderBy(n => rank.GetValueOrDefault(n.Id, int.MaxValue))];
        return new SetOrder(before, Label);
    }
}

/// <summary>The diagram's title and project link.</summary>
public sealed record SetDiagramInfo(string Title, string? Project) : IDiagramOperation
{
    public string Name => L.T("Diagram Properties");

    public IDiagramOperation Apply(Diagram diagram)
    {
        var undo = new SetDiagramInfo(diagram.Title, diagram.Project);
        diagram.Title = Title;
        diagram.Project = Project;
        return undo;
    }
}

/// <summary>Several operations as one step (one Undo).</summary>
public sealed record Batch(string Name, IReadOnlyList<IDiagramOperation> Operations) : IDiagramOperation
{
    public IDiagramOperation Apply(Diagram diagram)
    {
        var undo = new List<IDiagramOperation>();
        try
        {
            foreach (var op in Operations)
                undo.Add(op.Apply(diagram));
        }
        catch
        {
            // All or nothing: what already happened is undone before the error goes on.
            for (var i = undo.Count - 1; i >= 0; i--)
                undo[i].Apply(diagram);
            throw;
        }
        undo.Reverse();
        return new Batch(Name, undo);
    }
}

/// <summary>A change the diagram's rules don't allow, with a sentence for people.</summary>
public sealed class DiagramRuleException(string message) : Exception(message);
