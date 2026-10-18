using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevHub.Diagrams.Core;

/// <summary>A point in diagram coordinates (device-independent pixels, y down).</summary>
public readonly record struct DPoint(double X, double Y)
{
    public static DPoint operator +(DPoint a, DPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static DPoint operator -(DPoint a, DPoint b) => new(a.X - b.X, a.Y - b.Y);
    public static DPoint operator *(DPoint a, double k) => new(a.X * k, a.Y * k);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public double DistanceTo(DPoint other) => (this - other).Length;
}

/// <summary>A rectangle in diagram coordinates.</summary>
public readonly record struct DRect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public DPoint Center => new(X + Width / 2, Y + Height / 2);

    public bool Contains(DPoint p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    public bool Contains(DRect r) => r.X >= X && r.Right <= Right && r.Y >= Y && r.Bottom <= Bottom;

    public bool Intersects(DRect r) => r.X < Right && r.Right > X && r.Y < Bottom && r.Bottom > Y;

    public DRect Inflate(double by) => new(X - by, Y - by, Width + 2 * by, Height + 2 * by);

    public DRect Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    public DRect Union(DRect r)
    {
        var left = Math.Min(X, r.X);
        var top = Math.Min(Y, r.Y);
        return new DRect(left, top, Math.Max(Right, r.Right) - left, Math.Max(Bottom, r.Bottom) - top);
    }

    /// <summary>The rectangle spanned by two corners, in any order (a rubber band).</summary>
    public static DRect FromCorners(DPoint a, DPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>The smallest rectangle around all of them (empty input: a zero rectangle).</summary>
    public static DRect Around(IEnumerable<DRect> rects)
    {
        DRect? all = null;
        foreach (var r in rects)
            all = all is { } a ? a.Union(r) : r;
        return all ?? default;
    }
}

/// <summary>How an edge goes from shape to shape.</summary>
public enum EdgeRouting
{
    /// <summary>One straight line (through any waypoints).</summary>
    Straight,
    /// <summary>Horizontal and vertical segments, around the other shapes.</summary>
    Orthogonal,
}

/// <summary>
/// A diagram file (<c>*.devhub-diagram.json</c>): a kind ("usecase", "activity", "class", "merise", "moodboard"),
/// shapes (nodes) and lines (edges). What a shape or line means is its <see cref="DiagramNode.Type"/> and, for
/// details, its <see cref="DiagramNode.Props"/>; the kind that owns the diagram gives them meaning. Fields this
/// version doesn't know stay in <c>Extra</c> and are written back unchanged.
/// </summary>
public sealed class Diagram
{
    /// <summary>The file format's version; files with a newer one open read-only.</summary>
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;

    /// <summary>The kind of diagram (an <see cref="IDiagramKindModel.Id"/>).</summary>
    public string Kind { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>
    /// The project the diagram belongs to: its folder relative to the diagram file ("../.." for a file in
    /// <c>docs/diagrams/</c>), so the link survives moving the whole project. Null = not linked.
    /// </summary>
    public string? Project { get; set; }

    /// <summary>Kind-wide settings (e.g. "lanes": "horizontal", "sqlDialect": "postgresql").</summary>
    public Dictionary<string, string> Props { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Shapes, back to front.</summary>
    public List<DiagramNode> Nodes { get; set; } = [];

    public List<DiagramEdge> Edges { get; set; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public DiagramNode? Node(string? id) => id is null ? null : Nodes.Find(n => n.Id == id);

    public DiagramEdge? Edge(string? id) => id is null ? null : Edges.Find(e => e.Id == id);

    /// <summary>Whether an id is used by a node or an edge.</summary>
    public bool Has(string id) => Node(id) is not null || Edge(id) is not null;

    /// <summary>The nodes inside a container (directly).</summary>
    public IEnumerable<DiagramNode> Children(string id) => Nodes.Where(n => n.ParentId == id);

    /// <summary>A node and everything inside it, at any depth.</summary>
    public IEnumerable<DiagramNode> WithDescendants(string id)
    {
        if (Node(id) is not { } node)
            yield break;
        yield return node;
        foreach (var child in Children(id).ToList())
            foreach (var n in WithDescendants(child.Id))
                yield return n;
    }

    /// <summary>The edges touching a node.</summary>
    public IEnumerable<DiagramEdge> EdgesOf(string nodeId) => Edges.Where(e => e.From == nodeId || e.To == nodeId);

    /// <summary>The area the diagram covers (nodes and edge waypoints).</summary>
    public DRect Bounds => DRect.Around(Nodes.Select(n => n.Bounds)
        .Concat(Edges.SelectMany(e => e.Waypoints).Select(p => new DRect(p.X, p.Y, 0, 0))));

    /// <summary>A deep copy (through JSON, so unknown fields come along).</summary>
    public Diagram Clone() => DiagramFile.Read(DiagramFile.Write(this));
}

/// <summary>A shape: an actor, a use case, an action, a class, an entity, a picture, a stroke…</summary>
public sealed class DiagramNode
{
    public string Id { get; set; } = "";

    /// <summary>What it is, owned by the kind ("actor", "usecase", "boundary", "action", "class", "image"…).</summary>
    public string Type { get; set; } = "";

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>Degrees, clockwise (mood board pictures; fork bars turn by 90).</summary>
    public double Rotation { get; set; }

    /// <summary>The main text (a name, an action, a class's name).</summary>
    public string Text { get; set; } = "";

    /// <summary>The container it sits in (a system boundary, a swimlane, an activity, a frame); null = the canvas.</summary>
    public string? ParentId { get; set; }

    /// <summary>Details the kind defines ("attributes", "stereotype", "color", "url", "src"…).</summary>
    public Dictionary<string, string> Props { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Freehand strokes and lines: their points, relative to <see cref="X"/>, <see cref="Y"/>.</summary>
    public List<DPoint>? Points { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public DRect Bounds => new(X, Y, Width, Height);

    [JsonIgnore] public DPoint Center => Bounds.Center;

    public string Prop(string key, string fallback = "") => Props.TryGetValue(key, out var v) ? v : fallback;

    public bool Flag(string key) => Props.TryGetValue(key, out var v) && v == "true";
}

/// <summary>A line between two shapes: an association, a flow, an «include», a relation between entities…</summary>
public sealed class DiagramEdge
{
    public string Id { get; set; } = "";

    /// <summary>What it is, owned by the kind ("association", "include", "flow", "generalization"…).</summary>
    public string Type { get; set; } = "";

    public string From { get; set; } = "";

    public string To { get; set; } = "";

    /// <summary>The label in the middle (a guard "[valide]", a stereotype, an association's name).</summary>
    public string Text { get; set; } = "";

    public EdgeRouting Routing { get; set; } = EdgeRouting.Straight;

    /// <summary>Points the line goes through, set by hand; empty = routed automatically.</summary>
    public List<DPoint> Waypoints { get; set; } = [];

    /// <summary>Where the label sits along the line (0 = start, 1 = end); null = the middle.</summary>
    public double? LabelAt { get; set; }

    /// <summary>Details the kind defines ("fromMultiplicity", "toRole", "cardinality"…).</summary>
    public Dictionary<string, string> Props { get; set; } = new(StringComparer.Ordinal);

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public string Prop(string key, string fallback = "") => Props.TryGetValue(key, out var v) ? v : fallback;

    public bool Flag(string key) => Props.TryGetValue(key, out var v) && v == "true";
}
