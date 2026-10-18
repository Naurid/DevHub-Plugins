using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Uml.Core;

/// <summary>
/// Activity diagrams: start, end and flow end (⊗), actions, activities holding actions, decisions / merges with
/// [guards] on their branches, fork / join bars, object nodes, sent and received signals, swimlanes (partitions).
/// </summary>
public sealed class ActivityModel : IDiagramKindModel
{
    public const string KindId = "activity";

    public const string Start = "start";
    public const string End = "end";
    public const string FlowEnd = "flowEnd";
    public const string Action = "action";
    public const string Activity = "activity";
    public const string Decision = "decision";
    public const string Bar = "bar";
    public const string Object = "object";
    public const string SendSignal = "send";
    public const string ReceiveSignal = "receive";
    public const string Lane = "lane";
    public const string Note = "note";

    public const string Flow = "flow";
    public const string NoteLink = "noteLink";

    public string Id => KindId;
    public string Name => L.T("Activity diagram");
    public string Description => L.T("How a process runs: actions, decisions, parallel flows, swimlanes for who does what.");

    public IReadOnlyList<NodeTypeInfo> NodeTypes { get; } =
    [
        new(Start, L.T("Start"), NodeShape.Ellipse, 26, 26) { Description = L.T("Where the process begins (there can be several).") },
        new(Action, L.T("Action"), NodeShape.RoundedRectangle, 150, 50, L.T("Action")) { Description = L.T("One step of the process.") },
        new(Decision, L.T("Decision / merge"), NodeShape.Diamond, 34, 34)
            { Description = L.T("A choice between branches, each with its [condition]; also where branches meet again.") },
        new(Bar, L.T("Fork / join"), NodeShape.Bar, 140, 8)
            { Description = L.T("Splits the flow into parallel flows, or waits for all of them (make it tall for a vertical bar).") },
        new(End, L.T("End"), NodeShape.Ellipse, 30, 30) { Description = L.T("Where the whole process ends.") },
        new(FlowEnd, L.T("Flow end"), NodeShape.Ellipse, 26, 26) { Description = L.T("Ends one branch only; the rest goes on.") },
        new(Activity, L.T("Activity"), NodeShape.RoundedRectangle, 340, 200, L.T("Activity"))
            { IsContainer = true, Description = L.T("A group of actions with a name (a sub-process).") },
        new(Object, L.T("Object"), NodeShape.Rectangle, 120, 46, L.T("Object")) { Description = L.T("A thing an action makes or uses (an order, an invoice).") },
        new(SendSignal, L.T("Send signal"), NodeShape.Rectangle, 150, 46, L.T("Signal")) { Description = L.T("Sends a message elsewhere (a confirmation, a request).") },
        new(ReceiveSignal, L.T("Receive signal"), NodeShape.Rectangle, 150, 46, L.T("Signal")) { Description = L.T("Waits for a message from elsewhere.") },
        new(Lane, L.T("Swimlane"), NodeShape.Rectangle, 760, 220, L.T("Role"))
            { IsContainer = true, Connectable = false, Description = L.T("Who does the actions inside it (a role, a department, a system).") },
        new(Note, L.T("Note"), NodeShape.Rectangle, 150, 64, L.T("Note")) { Description = L.T("A comment, tied to shapes with a dashed line.") },
    ];

    public IReadOnlyList<EdgeTypeInfo> EdgeTypes { get; } =
    [
        new(Flow, L.T("Flow"), L.T("What comes next; after a decision, write the branch's [condition]."))
            { Allowed = (a, b) => a is not (Note or Lane) && b is not (Note or Lane), Routing = EdgeRouting.Orthogonal },
        new(NoteLink, L.T("Note link"), L.T("Ties a note to a shape.")) { Allowed = (a, b) => a == Note || b == Note },
    ];

    public IReadOnlyList<IDiagramRule> Rules { get; } =
    [
        new Rule(NoStart),
        new Rule(WrongDirection),
        new Rule(Guards),
        new Rule(Unreachable),
        new Rule(Forks),
    ];

    public IReadOnlyList<IDiagramTextExporter> TextExporters { get; } = [new ActivityPlantUml(), new ActivityMermaid()];

    public IReadOnlyList<DiagramSample> Samples => UmlSamples.Activity;

    public Diagram Create(string title)
    {
        var d = new Diagram { Kind = KindId, Title = title };
        var start = this.NewNode(d, Start, new DPoint(80, 80));
        d.Nodes.Add(start);
        return d;
    }

    private static IEnumerable<DiagramProblem> NoStart(Diagram d)
    {
        if (d.Nodes.Any(n => n.Type is Action or Decision) && !d.Nodes.Any(n => n.Type == Start))
            yield return new DiagramProblem(ProblemSeverity.Warning, L.T("The process has no start (a filled dot)."), []);
    }

    private static IEnumerable<DiagramProblem> WrongDirection(Diagram d)
    {
        foreach (var e in d.Edges.Where(e => e.Type == Flow))
        {
            if (d.Node(e.To)?.Type == Start)
                yield return new DiagramProblem(ProblemSeverity.Error, L.T("Nothing flows into a start: it's where the process begins."), [e.Id]);
            if (d.Node(e.From)?.Type is End or FlowEnd)
                yield return new DiagramProblem(ProblemSeverity.Error, L.T("Nothing flows out of an end."), [e.Id]);
        }
    }

    private static IEnumerable<DiagramProblem> Guards(Diagram d)
    {
        foreach (var decision in d.Nodes.Where(n => n.Type == Decision))
        {
            var outgoing = d.Edges.Where(e => e.Type == Flow && e.From == decision.Id).ToList();
            if (outgoing.Count < 2)
                continue;
            var unnamed = outgoing.Where(e => e.Text.Trim().Length == 0).ToList();
            if (unnamed.Count > 0)
                yield return new DiagramProblem(ProblemSeverity.Warning,
                    L.T("Each branch of a decision needs its condition, like [valid] or [otherwise]."), [decision.Id, .. unnamed.Select(e => e.Id)]);
        }
    }

    private static IEnumerable<DiagramProblem> Unreachable(Diagram d)
    {
        var starts = d.Nodes.Where(n => n.Type is Start or ReceiveSignal).Select(n => n.Id).ToList();
        if (starts.Count == 0)
            yield break;
        var flows = d.Edges.Where(e => e.Type == Flow).ToList();
        var reached = new HashSet<string>(starts);
        var queue = new Queue<string>(starts);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var e in flows.Where(e => e.From == id))
                if (reached.Add(e.To))
                    queue.Enqueue(e.To);
            // Object nodes feed actions: an action fed only by an object counts as reached through it.
        }
        foreach (var n in d.Nodes.Where(n => n.Type is Action or Decision or Bar or End or SendSignal && !reached.Contains(n.Id)))
            if (!flows.Any(e => e.To == n.Id && d.Node(e.From)?.Type == Object))
                yield return new DiagramProblem(ProblemSeverity.Warning,
                    L.T($"Nothing leads to \"{(n.Text.Length > 0 ? n.Text : NodeName(n))}\" from a start."), [n.Id]);
    }

    private static string NodeName(DiagramNode n) => n.Type switch
    {
        Decision => L.T("decision"),
        Bar => L.T("fork / join"),
        End => L.T("end"),
        _ => n.Type,
    };

    private static IEnumerable<DiagramProblem> Forks(Diagram d)
    {
        foreach (var bar in d.Nodes.Where(n => n.Type == Bar))
        {
            var ins = d.Edges.Count(e => e.Type == Flow && e.To == bar.Id);
            var outs = d.Edges.Count(e => e.Type == Flow && e.From == bar.Id);
            if (ins == 1 && outs == 1)
                yield return new DiagramProblem(ProblemSeverity.Info,
                    L.T("A fork splits one flow into several, a join gathers several into one: this bar has one of each."), [bar.Id]);
        }
    }
}

/// <summary>PlantUML (legacy activity syntax, which takes any graph).</summary>
public sealed class ActivityPlantUml : IDiagramTextExporter
{
    public string Name => "PlantUML";
    public string Extension => "puml";

    public string Export(Diagram d)
    {
        var w = new System.Text.StringBuilder("@startuml\n");
        if (d.Title.Length > 0)
            w.Append($"title {Text.Line(d.Title)}\n");
        string Ref(DiagramNode n) => n.Type switch
        {
            ActivityModel.Start or ActivityModel.End or ActivityModel.FlowEnd => "(*)",
            ActivityModel.Bar => $"==={n.Id}===",
            ActivityModel.Decision => $"\"{(n.Text.Length > 0 ? Text.Quote(n.Text) : "?")}\" as {n.Id}",
            _ => $"\"{Text.Quote(n.Text)}\" as {n.Id}",
        };
        foreach (var e in d.Edges.Where(e => e.Type == ActivityModel.Flow))
        {
            if (d.Node(e.From) is not { } a || d.Node(e.To) is not { } b)
                continue;
            var guard = e.Text.Trim().Length > 0 ? $" [{e.Text.Trim().Trim('[', ']')}]" : "";
            w.Append($"{Ref(a)} -->{guard} {Ref(b)}\n");
        }
        return w.Append("@enduml\n").ToString();
    }
}

/// <summary>Mermaid flowchart (GitHub and many wikis draw it).</summary>
public sealed class ActivityMermaid : IDiagramTextExporter
{
    public string Name => "Mermaid";
    public string Extension => "mmd";

    public string Export(Diagram d)
    {
        var w = new System.Text.StringBuilder("flowchart TD\n");
        foreach (var n in d.Nodes)
        {
            var label = Text.Quote(n.Text);
            var shape = n.Type switch
            {
                ActivityModel.Start => $"(( ))",
                ActivityModel.End => $"((( )))",
                ActivityModel.FlowEnd => "((x))",
                ActivityModel.Decision => $"{{\"{(label.Length > 0 ? label : " ")}\"}}",
                ActivityModel.Bar => "[ ]",
                ActivityModel.Object => $"[\"{label}\"]",
                ActivityModel.SendSignal => $">\"{label}\"]",
                ActivityModel.ReceiveSignal => $">\"{label}\"]",
                ActivityModel.Lane or ActivityModel.Activity or ActivityModel.Note => null,
                _ => $"(\"{label}\")",
            };
            if (shape is not null)
                w.Append($"  {n.Id}{shape}\n");
        }
        foreach (var lane in d.Nodes.Where(n => n.Type is ActivityModel.Lane or ActivityModel.Activity))
        {
            w.Append($"  subgraph {lane.Id}[\"{Text.Quote(lane.Text)}\"]\n");
            foreach (var child in d.Children(lane.Id).Where(c => c.Type is not (ActivityModel.Lane or ActivityModel.Activity or ActivityModel.Note)))
                w.Append($"    {child.Id}\n");
            w.Append("  end\n");
        }
        foreach (var e in d.Edges.Where(e => e.Type == ActivityModel.Flow))
        {
            var guard = e.Text.Trim().Trim('[', ']');
            w.Append(guard.Length > 0 ? $"  {e.From} -->|\"{Text.Quote(guard)}\"| {e.To}\n" : $"  {e.From} --> {e.To}\n");
        }
        return w.ToString();
    }
}
