using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Uml.Core;

/// <summary>
/// Sequence diagrams: lifelines (objects or actors) side by side, activation bars on them, messages straight across
/// (synchronous, asynchronous, reply, create), combined fragments (alt, opt, loop, par…) around messages. A message's
/// height is its first bend (drag it up or down); new messages go below the others.
/// </summary>
public sealed class SequenceModel : IDiagramKindModel
{
    public const string KindId = "sequence";

    public const string Lifeline = "lifeline";
    public const string Activation = "activation";
    public const string Fragment = "fragment";
    public const string Note = "note";

    public const string Sync = "sync";
    public const string Async = "async";
    public const string Reply = "reply";
    public const string CreateMessage = "create";
    public const string NoteLink = "noteLink";

    /// <summary>Height of a lifeline's head box.</summary>
    public const double HeadHeight = 40;

    public string Id => KindId;
    public string Name => L.T("Sequence diagram");
    public string Description => L.T("Who sends which message to whom, in order: lifelines, messages, replies, alt / loop fragments.");

    public IReadOnlyList<NodeTypeInfo> NodeTypes { get; } =
    [
        new(Lifeline, L.T("Lifeline"), NodeShape.Rectangle, 130, 420, L.T(":Object"))
            { IsContainer = true, Description = L.T("An object or a role taking part (\"name : Class\"); tick Actor for a person.") },
        new(Activation, L.T("Activation"), NodeShape.Rectangle, 14, 90)
            { Connectable = false, Description = L.T("When the lifeline is busy (put it on the dashed line).") },
        new(Fragment, L.T("Fragment"), NodeShape.Rectangle, 340, 160, "alt")
            { IsContainer = true, Connectable = false, Description = L.T("alt, opt, loop, par… around the messages it covers, with [conditions].") },
        new(Note, L.T("Note"), NodeShape.Rectangle, 150, 64, L.T("Note")) { Description = L.T("A comment.") },
    ];

    private static bool Lifelines(string a, string b) => a == Lifeline && b == Lifeline;

    public IReadOnlyList<EdgeTypeInfo> EdgeTypes { get; } =
    [
        new(Sync, L.T("Message"), L.T("A call that waits for the answer (filled arrow)."))
            { Allowed = Lifelines, DefaultText = L.T("message()") },
        new(Async, L.T("Asynchronous message"), L.T("A signal that doesn't wait (open arrow)."))
            { Allowed = Lifelines, DefaultText = L.T("signal") },
        new(Reply, L.T("Reply"), L.T("The answer to a message (dashed)."))
            { Allowed = Lifelines },
        new(CreateMessage, L.T("Create"), L.T("Makes the other object (dashed, «create»)."))
            { Allowed = Lifelines, DefaultText = "«create»" },
        new(NoteLink, L.T("Note link"), L.T("Ties a note to a shape.")) { Allowed = (a, b) => a == Note || b == Note },
    ];

    public IReadOnlyList<IDiagramRule> Rules { get; } = [new Rule(Unnamed)];

    public IReadOnlyList<IDiagramTextExporter> TextExporters { get; } = [new SequencePlantUml(), new SequenceMermaid()];

    public IReadOnlyList<DiagramSample> Samples => [.. UmlSamples.Sequence, .. UmlShowcase.Sequence];

    public Diagram Create(string title) => new() { Kind = KindId, Title = title };

    private static bool IsMessage(DiagramEdge e) => e.Type is Sync or Async or Reply or CreateMessage;

    /// <summary>The height a message runs at: its first bend, or below the messages before it.</summary>
    public static double MessageY(Diagram d, DiagramEdge e)
    {
        if (e.Waypoints.Count > 0)
            return e.Waypoints[0].Y;
        var top = d.Nodes.Where(n => n.Type == Lifeline).Select(n => n.Y).DefaultIfEmpty(0).Min() + HeadHeight + 30;
        var index = d.Edges.Where(IsMessage).TakeWhile(m => m != e).Count();
        return top + index * 40;
    }

    /// <summary>The messages from top to bottom (the order they happen in).</summary>
    public static IReadOnlyList<DiagramEdge> Ordered(Diagram d) => d.Edges.Where(IsMessage).OrderBy(e => MessageY(d, e)).ToList();

    public IReadOnlyList<DPoint>? CustomPath(Diagram d, DiagramEdge e)
    {
        if (!IsMessage(e) || d.Node(e.From) is not { } from || d.Node(e.To) is not { } to)
            return null;
        var y = MessageY(d, e);
        double X(DiagramNode lifeline, bool right)
        {
            // Onto the edge of an activation bar covering that height, if there is one.
            var bar = d.Children(lifeline.Id).FirstOrDefault(c => c.Type == Activation && c.Y <= y && c.Y + c.Height >= y);
            return bar is null ? lifeline.Center.X : right ? bar.X + bar.Width : bar.X;
        }
        if (from == to)
        {
            var x = X(from, true);
            return [new DPoint(x, y), new DPoint(x + 40, y), new DPoint(x + 40, y + 24), new DPoint(X(from, true), y + 24)];
        }
        var rightwards = to.Center.X > from.Center.X;
        return [new DPoint(X(from, rightwards), y), new DPoint(X(to, !rightwards), y)];
    }

    private static IEnumerable<DiagramProblem> Unnamed(Diagram d)
    {
        foreach (var e in d.Edges.Where(e => e.Type is Sync or Async && e.Text.Trim().Length == 0))
            yield return new DiagramProblem(ProblemSeverity.Warning, L.T("A message needs a name (the operation or the signal)."), [e.Id]);
    }
}

/// <summary>PlantUML sequence diagram (messages in order, fragments as groups).</summary>
public sealed class SequencePlantUml : IDiagramTextExporter
{
    public string Name => "PlantUML";
    public string Extension => "puml";

    public string Export(Diagram d)
    {
        var w = new System.Text.StringBuilder("@startuml\n");
        if (d.Title.Length > 0)
            w.Append($"title {Text.Line(d.Title)}\n");
        foreach (var l in d.Nodes.Where(n => n.Type == SequenceModel.Lifeline).OrderBy(n => n.X))
            w.Append($"{(l.Flag("actor") ? "actor" : "participant")} \"{Text.Quote(l.Text)}\" as {l.Id}\n");
        var fragments = d.Nodes.Where(n => n.Type == SequenceModel.Fragment).ToList();
        var open = new List<DiagramNode>();
        foreach (var e in SequenceModel.Ordered(d))
        {
            var y = SequenceModel.MessageY(d, e);
            foreach (var f in open.Where(f => y > f.Y + f.Height).ToList())
            {
                w.Append("end\n");
                open.Remove(f);
            }
            foreach (var f in fragments.Where(f => !open.Contains(f) && f.Y <= y && y <= f.Y + f.Height).OrderBy(f => f.Y))
            {
                var guard = f.Prop("guard");
                w.Append($"{(f.Text.Length > 0 ? f.Text.Trim() : "group")}{(guard.Length > 0 ? " " + guard : "")}\n");
                open.Add(f);
            }
            var arrow = e.Type switch
            {
                SequenceModel.Async => "->>",
                SequenceModel.Reply => "-->",
                SequenceModel.CreateMessage => "-->",
                _ => "->",
            };
            w.Append($"{e.From} {arrow} {e.To} : {Text.Line(e.Text)}\n");
        }
        foreach (var _ in open)
            w.Append("end\n");
        return w.Append("@enduml\n").ToString();
    }
}

/// <summary>Mermaid sequence diagram.</summary>
public sealed class SequenceMermaid : IDiagramTextExporter
{
    public string Name => "Mermaid";
    public string Extension => "mmd";

    public string Export(Diagram d)
    {
        var w = new System.Text.StringBuilder("sequenceDiagram\n");
        foreach (var l in d.Nodes.Where(n => n.Type == SequenceModel.Lifeline).OrderBy(n => n.X))
            w.Append($"  {(l.Flag("actor") ? "actor" : "participant")} {l.Id} as {Text.Line(l.Text)}\n");
        foreach (var e in SequenceModel.Ordered(d))
        {
            var arrow = e.Type switch
            {
                SequenceModel.Async => "-)",
                SequenceModel.Reply => "-->>",
                SequenceModel.CreateMessage => "-->>",
                _ => "->>",
            };
            w.Append($"  {e.From}{arrow}{e.To}: {Text.Line(e.Text)}\n");
        }
        return w.ToString();
    }
}
