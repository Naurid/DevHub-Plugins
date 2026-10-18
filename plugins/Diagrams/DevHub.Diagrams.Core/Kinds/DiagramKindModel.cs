namespace DevHub.Diagrams.Core;

/// <summary>A shape a kind offers in its palette ("Actor", "Use case", "System boundary").</summary>
/// <param name="Type">Stored in <see cref="DiagramNode.Type"/>.</param>
/// <param name="Name">Shown in the palette (translated).</param>
/// <param name="DefaultText">The text a new one gets ("Acteur").</param>
public sealed record NodeTypeInfo(string Type, string Name, NodeShape Shape, double Width, double Height, string DefaultText = "")
{
    /// <summary>Other shapes can sit inside it (a system boundary, a swimlane, a frame).</summary>
    public bool IsContainer { get; init; }

    /// <summary>The palette's icon (an <c>IconKind</c> name), or null for the kind's own drawing.</summary>
    public string? Icon { get; init; }

    /// <summary>A short explanation for the palette's tooltip (translated).</summary>
    public string Description { get; init; } = "";

    /// <summary>Lines can start or end on it.</summary>
    public bool Connectable { get; init; } = true;

    /// <summary>Kind details a new one starts with.</summary>
    public IReadOnlyDictionary<string, string> Props { get; init; } = new Dictionary<string, string>();

    /// <summary>Hidden from the palette (made by a tool or a converter, e.g. an MLD table).</summary>
    public bool Hidden { get; init; }
}

/// <summary>A line a kind offers ("Association", "«include»", "Flow").</summary>
public sealed record EdgeTypeInfo(string Type, string Name, string Description = "")
{
    /// <summary>Which node types it may join (from, to); null = any connectable shapes.</summary>
    public Func<string, string, bool>? Allowed { get; init; }

    /// <summary>The routing new ones get.</summary>
    public EdgeRouting Routing { get; init; } = EdgeRouting.Straight;

    /// <summary>The text a new one gets ("«include»").</summary>
    public string DefaultText { get; init; } = "";

    public IReadOnlyDictionary<string, string> Props { get; init; } = new Dictionary<string, string>();
}

/// <summary>Something wrong (or worth a look) in a diagram, by the kind's rules.</summary>
public enum ProblemSeverity { Info, Warning, Error }

/// <summary>A problem the problems list shows; clicking it selects <see cref="ElementIds"/>.</summary>
public sealed record DiagramProblem(ProblemSeverity Severity, string Message, IReadOnlyList<string> ElementIds);

/// <summary>A notation rule from the course ("«extend» goes from the extension to the base use case").</summary>
public interface IDiagramRule
{
    IEnumerable<DiagramProblem> Check(Diagram diagram);
}

/// <summary>A rule written as a function.</summary>
public sealed class Rule(Func<Diagram, IEnumerable<DiagramProblem>> check) : IDiagramRule
{
    public IEnumerable<DiagramProblem> Check(Diagram diagram) => check(diagram);
}

/// <summary>A text form of a diagram (PlantUML, Mermaid, SQL).</summary>
public interface IDiagramTextExporter
{
    /// <summary>"PlantUML", "Mermaid", "SQL (PostgreSQL)".</summary>
    string Name { get; }

    /// <summary>The file ending for Save ("puml", "mmd", "sql").</summary>
    string Extension { get; }

    string Export(Diagram diagram);
}

/// <summary>
/// What a kind of diagram is, without any drawing: its shapes and lines, the rules from the course, its text
/// exports. Lives in the kind plugin's <c>.Core</c> (tested); the UI side (<c>IDiagramKind</c>) adds the drawing.
/// </summary>
public interface IDiagramKindModel
{
    /// <summary>Stored in <see cref="Diagram.Kind"/> ("usecase", "activity", "class", "merise", "moodboard").</summary>
    string Id { get; }

    /// <summary>"Use case diagram" (translated).</summary>
    string Name { get; }

    string Description { get; }

    IReadOnlyList<NodeTypeInfo> NodeTypes { get; }

    IReadOnlyList<EdgeTypeInfo> EdgeTypes { get; }

    IReadOnlyList<IDiagramRule> Rules { get; }

    IReadOnlyList<IDiagramTextExporter> TextExporters { get; }

    /// <summary>A new diagram of this kind (maybe with a starting shape, e.g. a system boundary).</summary>
    Diagram Create(string title);

    /// <summary>
    /// A line drawn the kind's own way (a sequence diagram's message runs straight across between lifelines), or null
    /// for the usual routing.
    /// </summary>
    IReadOnlyList<DPoint>? CustomPath(Diagram diagram, DiagramEdge edge) => null;

    /// <summary>Ready-made diagrams to start from (course exercises), in the user's language.</summary>
    IReadOnlyList<DiagramSample> Samples => [];
}

/// <summary>Shared helpers for kind models.</summary>
public static class KindModels
{
    public static NodeTypeInfo? NodeType(this IDiagramKindModel kind, string type) => kind.NodeTypes.FirstOrDefault(t => t.Type == type);

    public static EdgeTypeInfo? EdgeType(this IDiagramKindModel kind, string type) => kind.EdgeTypes.FirstOrDefault(t => t.Type == type);

    /// <summary>The points a line is drawn through: the kind's own path, or routed between the shapes' outlines.</summary>
    public static IReadOnlyList<DPoint> PathOf(this IDiagramKindModel kind, Diagram diagram, DiagramEdge edge) =>
        kind.CustomPath(diagram, edge) ?? Routing.Path(diagram, edge, kind.ShapeOf);

    /// <summary>The outline of a node by its type (rectangle when the kind doesn't know it).</summary>
    public static NodeShape ShapeOf(this IDiagramKindModel kind, DiagramNode node) => kind.NodeType(node.Type)?.Shape ?? NodeShape.Rectangle;

    public static bool IsContainer(this IDiagramKindModel kind, DiagramNode node) => kind.NodeType(node.Type)?.IsContainer == true;

    /// <summary>All the problems the kind's rules find, errors first.</summary>
    public static IReadOnlyList<DiagramProblem> Check(this IDiagramKindModel kind, Diagram diagram) =>
        kind.Rules.SelectMany(r => r.Check(diagram)).OrderByDescending(p => p.Severity).ToList();

    /// <summary>A new node of a type at a point (its centre), with the type's size and defaults.</summary>
    public static DiagramNode NewNode(this IDiagramKindModel kind, Diagram diagram, string type, DPoint center, string? text = null)
    {
        var t = kind.NodeType(type) ?? throw new ArgumentException($"Unknown node type {type}");
        return new DiagramNode
        {
            Id = DiagramIds.NewIn(diagram),
            Type = type,
            X = center.X - t.Width / 2,
            Y = center.Y - t.Height / 2,
            Width = t.Width,
            Height = t.Height,
            Text = text ?? t.DefaultText,
            Props = new Dictionary<string, string>(t.Props, StringComparer.Ordinal),
        };
    }

    /// <summary>A new line of a type, or null when the kind doesn't allow it between these shapes.</summary>
    public static DiagramEdge? NewEdge(this IDiagramKindModel kind, Diagram diagram, string type, DiagramNode from, DiagramNode to)
    {
        var t = kind.EdgeType(type);
        if (t is null || !CanConnect(kind, t, from, to))
            return null;
        return new DiagramEdge
        {
            Id = DiagramIds.NewIn(diagram),
            Type = type,
            From = from.Id,
            To = to.Id,
            Text = t.DefaultText,
            Routing = t.Routing,
            Props = new Dictionary<string, string>(t.Props, StringComparer.Ordinal),
        };
    }

    public static bool CanConnect(this IDiagramKindModel kind, EdgeTypeInfo type, DiagramNode from, DiagramNode to) =>
        kind.NodeType(from.Type)?.Connectable != false && kind.NodeType(to.Type)?.Connectable != false
        && (type.Allowed?.Invoke(from.Type, to.Type) ?? true);
}

/// <summary>A ready-made diagram to start from: a course exercise, built in the user's language.</summary>
public sealed record DiagramSample(string Name, string Description, Func<Diagram> Create)
{
    /// <summary>Lays the sample out again once its shapes are sized to their text (a mind map's tree), or null.</summary>
    public Func<Diagram, IDiagramOperation>? Arrange { get; init; }

    /// <summary>
    /// Files the sample needs (pictures), by file name: written to <c>&lt;diagram&gt;.assets/</c> beside it; a <c>src</c>
    /// starting with <c>{assets}/</c> points there.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> Assets { get; init; } = new Dictionary<string, byte[]>();

    public const string AssetsToken = "{assets}/";
}

/// <summary>Writes a sample diagram in a few lines: shapes by centre or box, lines by type (refused lines throw).</summary>
public sealed class SampleBuilder(IDiagramKindModel kind, string title)
{
    // French typography puts no-break spaces before ":" in translations; in notation (+ nom : String, PK id INT) they
    // would reach PlantUML, Mermaid and SQL, so samples use plain spaces.
    private static string Plain(string text) => text.Replace(' ', ' ').Replace(' ', ' ');

    public Diagram Diagram { get; } = new() { Kind = kind.Id, Title = title };

    /// <summary>A shape of the type's size, centred on (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public DiagramNode Node(string type, double x, double y, string? text = null, DiagramNode? parent = null, params (string Key, string Value)[] props)
    {
        var node = kind.NewNode(Diagram, type, new DPoint(x, y), text is null ? null : Plain(text));
        node.ParentId = parent?.Id;
        foreach (var (key, value) in props)
            node.Props[key] = Plain(value);
        Diagram.Nodes.Add(node);
        return node;
    }

    /// <summary>A shape at a box (containers: lanes, pools, boundaries, lifelines).</summary>
    public DiagramNode Box(string type, double x, double y, double width, double height, string? text = null, DiagramNode? parent = null,
        params (string Key, string Value)[] props)
    {
        var node = Node(type, x + width / 2, y + height / 2, text, parent, props);
        (node.X, node.Y, node.Width, node.Height) = (x, y, width, height);
        return node;
    }

    public DiagramEdge Edge(string type, DiagramNode from, DiagramNode to, string text = "", params (string Key, string Value)[] props)
    {
        var edge = kind.NewEdge(Diagram, type, from, to) ?? throw new InvalidOperationException($"A {type} can't join {from.Type} to {to.Type}.");
        if (text.Length > 0)
            edge.Text = Plain(text);
        foreach (var (key, value) in props)
            edge.Props[key] = Plain(value);
        Diagram.Edges.Add(edge);
        return edge;
    }
}
