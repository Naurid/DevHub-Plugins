using System.Text;
using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Business.Core;

/// <summary>Flowcharts (ISO 5807 shapes): start / end, process, decision, input / output, document, database…</summary>
public sealed class FlowchartModel : IDiagramKindModel
{
    public const string KindId = "flowchart";

    public const string Terminator = "terminator";
    public const string Process = "process";
    public const string Decision = "decision";
    public const string InputOutput = "io";
    public const string Document = "document";
    public const string Predefined = "predefined";
    public const string ManualInput = "manualInput";
    public const string Database = "database";
    public const string Connector = "connector";
    public const string Note = "note";

    public const string Flow = "flow";
    public const string NoteLink = "noteLink";

    public string Id => KindId;
    public string Name => L.T("Flowchart");
    public string Description => L.T("Steps, decisions and their order, with the classic flowchart shapes.");

    public IReadOnlyList<NodeTypeInfo> NodeTypes { get; } =
    [
        new(Terminator, L.T("Start / end"), NodeShape.RoundedRectangle, 130, 46, L.T("Start")) { Description = L.T("Where the flow starts or stops.") },
        new(Process, L.T("Process"), NodeShape.Rectangle, 150, 60, L.T("Process")) { Description = L.T("A step that does something.") },
        new(Decision, L.T("Decision"), NodeShape.Diamond, 130, 80, L.T("Condition?")) { Description = L.T("A question; each way out is an answer (yes / no).") },
        new(InputOutput, L.T("Input / output"), NodeShape.Rectangle, 150, 56, L.T("Data")) { Description = L.T("Data read or shown.") },
        new(Document, L.T("Document"), NodeShape.Rectangle, 140, 70, L.T("Document")) { Description = L.T("A printed or written document.") },
        new(Predefined, L.T("Predefined process"), NodeShape.Rectangle, 150, 60, L.T("Subroutine")) { Description = L.T("A process described elsewhere.") },
        new(ManualInput, L.T("Manual input"), NodeShape.Rectangle, 150, 56, L.T("Input")) { Description = L.T("Something a person types or enters.") },
        new(Database, L.T("Database"), NodeShape.Rectangle, 110, 76, L.T("Database")) { Description = L.T("Stored data.") },
        new(Connector, L.T("Connector"), NodeShape.Ellipse, 34, 34, "A") { Description = L.T("Joins the flow to the same letter elsewhere on the page.") },
        new(Note, L.T("Note"), NodeShape.Rectangle, 150, 64, L.T("Note")) { Description = L.T("A comment, tied to shapes with a dashed line.") },
    ];

    public IReadOnlyList<EdgeTypeInfo> EdgeTypes { get; } =
    [
        new(Flow, L.T("Flow"), L.T("What comes next; out of a decision, write the answer (yes, no)."))
            { Allowed = (a, b) => a != Note && b != Note, Routing = EdgeRouting.Orthogonal },
        new(NoteLink, L.T("Note link"), L.T("Ties a note to a shape.")) { Allowed = (a, b) => a == Note || b == Note },
    ];

    public IReadOnlyList<IDiagramRule> Rules { get; } = [new Rule(Starts), new Rule(Decisions), new Rule(DeadEnds)];

    public IReadOnlyList<IDiagramTextExporter> TextExporters { get; } = [new FlowchartMermaid()];

    public IReadOnlyList<DiagramSample> Samples => BusinessSamples.Flowchart;

    public Diagram Create(string title)
    {
        var d = new Diagram { Kind = KindId, Title = title };
        d.Nodes.Add(this.NewNode(d, Terminator, new DPoint(200, 80)));
        return d;
    }

    private static IEnumerable<DiagramProblem> Starts(Diagram d)
    {
        if (d.Nodes.Any(n => n.Type is Process or Decision) && !d.Nodes.Any(n => n.Type == Terminator))
            yield return new DiagramProblem(ProblemSeverity.Warning, L.T("The flowchart has no start / end shape."), []);
    }

    private static IEnumerable<DiagramProblem> Decisions(Diagram d)
    {
        foreach (var n in d.Nodes.Where(n => n.Type == Decision))
        {
            var outs = d.Edges.Where(e => e.Type == Flow && e.From == n.Id).ToList();
            if (outs.Count < 2)
                yield return new DiagramProblem(ProblemSeverity.Warning, L.T("A decision has at least two ways out (yes and no)."), [n.Id]);
            else if (outs.Any(e => e.Text.Trim().Length == 0))
                yield return new DiagramProblem(ProblemSeverity.Info, L.T("Name each way out of a decision (yes, no …)."), [n.Id, .. outs.Where(e => e.Text.Trim().Length == 0).Select(e => e.Id)]);
        }
    }

    private static IEnumerable<DiagramProblem> DeadEnds(Diagram d)
    {
        foreach (var n in d.Nodes.Where(n => n.Type is Process or InputOutput or ManualInput or Predefined or Document))
            if (!d.Edges.Any(e => e.Type == Flow && e.From == n.Id) && d.Edges.Any(e => e.Type == Flow && e.To == n.Id))
                yield return new DiagramProblem(ProblemSeverity.Info, L.T($"\"{n.Text}\" leads nowhere: end the flow with an end shape."), [n.Id]);
    }
}

public sealed class FlowchartMermaid : IDiagramTextExporter
{
    public string Name => "Mermaid";
    public string Extension => "mmd";

    public string Export(Diagram d)
    {
        var w = new StringBuilder("flowchart TD\n");
        foreach (var n in d.Nodes.Where(n => n.Type != FlowchartModel.Note))
        {
            var t = Text.Quote(n.Text);
            w.Append("  ").Append(n.Id).Append(n.Type switch
            {
                FlowchartModel.Terminator => $"([\"{t}\"])",
                FlowchartModel.Decision => $"{{\"{t}\"}}",
                FlowchartModel.InputOutput => $"[/\"{t}\"/]",
                FlowchartModel.Predefined => $"[[\"{t}\"]]",
                FlowchartModel.ManualInput => $"[/\"{t}\"\\]",
                FlowchartModel.Database => $"[(\"{t}\")]",
                FlowchartModel.Connector => $"((\"{t}\"))",
                _ => $"[\"{t}\"]",
            }).Append('\n');
        }
        foreach (var e in d.Edges.Where(e => e.Type == FlowchartModel.Flow))
            w.Append(e.Text.Trim().Length > 0 ? $"  {e.From} -->|\"{Text.Quote(e.Text.Trim())}\"| {e.To}\n" : $"  {e.From} --> {e.To}\n");
        return w.ToString();
    }
}

/// <summary>
/// BPMN 2.0 processes: pools and lanes, events (start, intermediate, end, with their trigger), tasks, sub-processes,
/// gateways, data objects and stores; sequence flows inside a pool, message flows between pools.
/// </summary>
public sealed class BpmnModel : IDiagramKindModel
{
    public const string KindId = "bpmn";

    public const string Pool = "pool";
    public const string Lane = "lane";
    public const string StartEvent = "startEvent";
    public const string IntermediateEvent = "intermediateEvent";
    public const string EndEvent = "endEvent";
    public const string Task = "task";
    public const string SubProcess = "subProcess";
    public const string Gateway = "gateway";
    public const string DataObject = "dataObject";
    public const string DataStore = "dataStore";
    public const string Annotation = "annotation";

    public const string SequenceFlow = "sequenceFlow";
    public const string MessageFlow = "messageFlow";
    public const string Association = "association";

    public static readonly string[] Triggers = ["none", "message", "timer", "error", "signal", "conditional", "terminate"];
    public static readonly string[] TaskTypes = ["none", "user", "service", "send", "receive", "manual", "script", "businessRule"];
    public static readonly string[] GatewayTypes = ["exclusive", "parallel", "inclusive", "eventBased"];

    private static bool IsFlowNode(string t) => t is StartEvent or IntermediateEvent or EndEvent or Task or SubProcess or Gateway;

    public string Id => KindId;
    public string Name => L.T("Business process (BPMN)");
    public string Description => L.T("Who does what, in which order, and what they send each other: pools, lanes, tasks, events and gateways.");

    public IReadOnlyList<NodeTypeInfo> NodeTypes { get; } =
    [
        new(Pool, L.T("Pool"), NodeShape.Rectangle, 820, 260, L.T("Participant"))
            { IsContainer = true, Description = L.T("One participant (a company, a customer, a system), with its own process.") },
        new(Lane, L.T("Lane"), NodeShape.Rectangle, 790, 130, L.T("Role"))
            { IsContainer = true, Connectable = false, Description = L.T("A role or department inside a pool.") },
        new(StartEvent, L.T("Start event"), NodeShape.Ellipse, 36, 36) { Description = L.T("Where the process starts (thin circle).") },
        new(IntermediateEvent, L.T("Intermediate event"), NodeShape.Ellipse, 36, 36,
            "") { Description = L.T("Something that happens along the way: a message, a timer (double circle)."), Props = new Dictionary<string, string> { ["trigger"] = "timer" } },
        new(EndEvent, L.T("End event"), NodeShape.Ellipse, 36, 36) { Description = L.T("Where the process ends (thick circle).") },
        new(Task, L.T("Task"), NodeShape.RoundedRectangle, 130, 70, L.T("Task")) { Description = L.T("One piece of work.") },
        new(SubProcess, L.T("Sub-process"), NodeShape.RoundedRectangle, 140, 76, L.T("Sub-process")) { Description = L.T("Work described in another diagram ([+]).") },
        new(Gateway, L.T("Gateway"), NodeShape.Diamond, 46, 46)
            { Description = L.T("Splits or joins the flow: exclusive (X, one way), parallel (+, all ways), inclusive (O, some ways)."), Props = new Dictionary<string, string> { ["gatewayType"] = "exclusive" } },
        new(DataObject, L.T("Data object"), NodeShape.Rectangle, 40, 52, L.T("Data")) { Description = L.T("A document or data the process uses or makes.") },
        new(DataStore, L.T("Data store"), NodeShape.Rectangle, 56, 50, L.T("Store")) { Description = L.T("Where data is kept (a database).") },
        new(Annotation, L.T("Text annotation"), NodeShape.Rectangle, 140, 50, L.T("Comment")) { Description = L.T("A remark, tied with a dotted line.") },
    ];

    public IReadOnlyList<EdgeTypeInfo> EdgeTypes { get; } =
    [
        new(SequenceFlow, L.T("Sequence flow"), L.T("What comes next, inside one pool."))
            { Allowed = (a, b) => IsFlowNode(a) && IsFlowNode(b), Routing = EdgeRouting.Orthogonal },
        new(MessageFlow, L.T("Message flow"), L.T("What one pool sends another (dashed, between pools).")) { Allowed = (a, b) => a is not (Annotation or Lane) && b is not (Annotation or Lane) },
        new(Association, L.T("Association"), L.T("Ties data or an annotation to a shape (dotted).")),
    ];

    public IReadOnlyList<IDiagramRule> Rules { get; } = [new Rule(Flows), new Rule(Events), new Rule(Gateways), new Rule(Lanes)];

    public IReadOnlyList<IDiagramTextExporter> TextExporters { get; } = [new BpmnOutline()];

    public IReadOnlyList<DiagramSample> Samples => BusinessSamples.Bpmn;

    public Diagram Create(string title)
    {
        var d = new Diagram { Kind = KindId, Title = title };
        var pool = this.NewNode(d, Pool, new DPoint(460, 200));
        d.Nodes.Add(pool);
        var start = this.NewNode(d, StartEvent, new DPoint(pool.X + 90, pool.Y + pool.Height / 2));
        start.ParentId = pool.Id;
        d.Nodes.Add(start);
        return d;
    }

    /// <summary>The pool a shape is in (through its lanes), or null.</summary>
    public static DiagramNode? PoolOf(Diagram d, DiagramNode n)
    {
        for (var p = n; p is not null; p = p.ParentId is { } id ? d.Node(id) : null)
            if (p.Type == Pool)
                return p;
        return null;
    }

    private static IEnumerable<DiagramProblem> Flows(Diagram d)
    {
        foreach (var e in d.Edges)
        {
            if (d.Node(e.From) is not { } a || d.Node(e.To) is not { } b)
                continue;
            var pa = PoolOf(d, a);
            var pb = PoolOf(d, b);
            if (e.Type == SequenceFlow && pa != pb)
                yield return new DiagramProblem(ProblemSeverity.Error, L.T("A sequence flow stays inside one pool; between pools, use a message flow."), [e.Id]);
            if (e.Type == MessageFlow && pa is not null && pa == pb)
                yield return new DiagramProblem(ProblemSeverity.Error, L.T("A message flow goes between two pools; inside a pool, use a sequence flow."), [e.Id]);
        }
    }

    private static IEnumerable<DiagramProblem> Events(Diagram d)
    {
        foreach (var e in d.Edges.Where(e => e.Type == SequenceFlow))
        {
            if (d.Node(e.To)?.Type == StartEvent)
                yield return new DiagramProblem(ProblemSeverity.Error, L.T("No sequence flow comes into a start event."), [e.Id]);
            if (d.Node(e.From)?.Type == EndEvent)
                yield return new DiagramProblem(ProblemSeverity.Error, L.T("No sequence flow leaves an end event."), [e.Id]);
        }
        foreach (var pool in d.Nodes.Where(n => n.Type == Pool))
        {
            var inside = d.WithDescendants(pool.Id).ToList();
            if (!inside.Any(n => n.Type is Task or SubProcess))
                continue;
            if (!inside.Any(n => n.Type == StartEvent))
                yield return new DiagramProblem(ProblemSeverity.Warning, L.T($"The pool \"{pool.Text}\" has no start event."), [pool.Id]);
            if (!inside.Any(n => n.Type == EndEvent))
                yield return new DiagramProblem(ProblemSeverity.Warning, L.T($"The pool \"{pool.Text}\" has no end event."), [pool.Id]);
        }
    }

    private static IEnumerable<DiagramProblem> Gateways(Diagram d)
    {
        foreach (var g in d.Nodes.Where(n => n.Type == Gateway && n.Prop("gatewayType", "exclusive") is "exclusive" or "inclusive"))
        {
            var outs = d.Edges.Where(e => e.Type == SequenceFlow && e.From == g.Id).ToList();
            if (outs.Count > 1 && outs.Any(e => e.Text.Trim().Length == 0))
                yield return new DiagramProblem(ProblemSeverity.Info,
                    L.T("Name the conditions out of an exclusive or inclusive gateway (one may be the default)."), [g.Id]);
        }
    }

    private static IEnumerable<DiagramProblem> Lanes(Diagram d)
    {
        foreach (var lane in d.Nodes.Where(n => n.Type == Lane && (n.ParentId is not { } p || d.Node(p)?.Type is not (Pool or Lane))))
            yield return new DiagramProblem(ProblemSeverity.Warning, L.T("A lane sits inside a pool."), [lane.Id]);
    }
}

/// <summary>The process as a numbered outline, pool by pool (for a report or a ticket).</summary>
public sealed class BpmnOutline : IDiagramTextExporter
{
    public string Name => L.T("Outline (Markdown)");
    public string Extension => "md";

    public string Export(Diagram d)
    {
        var w = new StringBuilder();
        if (d.Title.Length > 0)
            w.Append("# ").Append(Text.Line(d.Title)).Append("\n\n");
        foreach (var pool in d.Nodes.Where(n => n.Type == BpmnModel.Pool))
        {
            w.Append("## ").Append(Text.Line(pool.Text)).Append("\n\n");
            var inside = d.WithDescendants(pool.Id).Where(n => n.Type is BpmnModel.Task or BpmnModel.SubProcess or BpmnModel.StartEvent
                or BpmnModel.IntermediateEvent or BpmnModel.EndEvent or BpmnModel.Gateway).ToList();
            var order = Walk(d, inside);
            var i = 1;
            foreach (var n in order)
            {
                var lane = n.ParentId is { } p && d.Node(p) is { Type: BpmnModel.Lane } l ? $" — *{Text.Line(l.Text)}*" : "";
                var what = n.Type switch
                {
                    BpmnModel.StartEvent => L.T("Start"),
                    BpmnModel.EndEvent => L.T("End"),
                    BpmnModel.Gateway => L.T("Decision"),
                    BpmnModel.IntermediateEvent => L.T("Event"),
                    _ => "",
                };
                var label = string.Join(": ", new[] { what, Text.Line(n.Text) }.Where(s => s.Length > 0));
                w.Append($"{i++}. {label}{lane}\n");
                foreach (var e in d.Edges.Where(e => e.Type == BpmnModel.SequenceFlow && e.From == n.Id && e.Text.Trim().Length > 0))
                    w.Append($"   - {Text.Line(e.Text)} → {Text.Line(d.Node(e.To)?.Text ?? "")}\n");
                foreach (var e in d.Edges.Where(e => e.Type == BpmnModel.MessageFlow && e.From == n.Id))
                    w.Append($"   - ✉ {Text.Line(e.Text.Length > 0 ? e.Text : L.T("message"))} → {Text.Line(d.Node(e.To) is { } t ? BpmnModel.PoolOf(d, t)?.Text ?? t.Text : "")}\n");
            }
            w.Append('\n');
        }
        return w.ToString();
    }

    /// <summary>Shapes in flow order from the start events (then whatever wasn't reached).</summary>
    private static List<DiagramNode> Walk(Diagram d, List<DiagramNode> nodes)
    {
        var set = nodes.Select(n => n.Id).ToHashSet();
        var seen = new HashSet<string>();
        var order = new List<DiagramNode>();
        var queue = new Queue<DiagramNode>(nodes.Where(n => n.Type == BpmnModel.StartEvent).OrderBy(n => n.Y).ThenBy(n => n.X));
        while (queue.Count > 0 || order.Count < nodes.Count)
        {
            if (queue.Count == 0)
                queue.Enqueue(nodes.Where(n => !seen.Contains(n.Id)).OrderBy(n => n.X).First());
            var n = queue.Dequeue();
            if (!seen.Add(n.Id))
                continue;
            order.Add(n);
            foreach (var e in d.Edges.Where(e => e.Type == BpmnModel.SequenceFlow && e.From == n.Id && set.Contains(e.To)))
                if (d.Node(e.To) is { } next && !seen.Contains(next.Id))
                    queue.Enqueue(next);
        }
        return order;
    }
}

/// <summary>Mind maps: a central idea, topics around it, sub-topics, joined by curved branches.</summary>
public sealed class MindMapModel : IDiagramKindModel
{
    public const string KindId = "mindmap";

    public const string Central = "central";
    public const string Topic = "topic";
    public const string SubTopic = "subtopic";
    public const string Note = "note";

    public const string Branch = "branch";

    /// <summary>Branch colours, one per main topic in turn.</summary>
    public static readonly string[] Colours = ["#E5534B", "#3574F0", "#2E9E5B", "#D98C1F", "#8E5BD6", "#1FA3B8", "#C94F9A"];

    public string Id => KindId;
    public string Name => L.T("Mind map");
    public string Description => L.T("Ideas around a central subject, for brainstorming and planning.");

    public IReadOnlyList<NodeTypeInfo> NodeTypes { get; } =
    [
        new(Central, L.T("Central idea"), NodeShape.Ellipse, 180, 80, L.T("Subject")) { Description = L.T("What the map is about.") },
        new(Topic, L.T("Topic"), NodeShape.RoundedRectangle, 140, 44, L.T("Topic")) { Description = L.T("A main idea around the subject.") },
        new(SubTopic, L.T("Sub-topic"), NodeShape.Rectangle, 120, 30, L.T("Idea")) { Description = L.T("A detail of a topic (Tools › Add Idea adds one to the selected idea).") },
        new(Note, L.T("Note"), NodeShape.Rectangle, 150, 64, L.T("Note")) { Description = L.T("A longer remark.") },
    ];

    public IReadOnlyList<EdgeTypeInfo> EdgeTypes { get; } =
    [
        new(Branch, L.T("Branch"), L.T("From an idea to a more detailed one.")),
    ];

    public IReadOnlyList<IDiagramRule> Rules { get; } = [new Rule(Orphans)];

    public IReadOnlyList<IDiagramTextExporter> TextExporters { get; } = [new MindMapOutline(), new MindMapMermaid()];

    public IReadOnlyList<DiagramSample> Samples => BusinessSamples.MindMap;

    public Diagram Create(string title)
    {
        var d = new Diagram { Kind = KindId, Title = title };
        d.Nodes.Add(this.NewNode(d, Central, new DPoint(400, 300), title.Length > 0 ? title : null));
        return d;
    }

    /// <summary>The idea a shape hangs from (the start of its incoming branch).</summary>
    public static DiagramNode? ParentOf(Diagram d, DiagramNode n) =>
        d.Edges.FirstOrDefault(e => e.Type == Branch && e.To == n.Id) is { } e ? d.Node(e.From) : null;

    public static IEnumerable<DiagramNode> ChildrenOf(Diagram d, DiagramNode n) =>
        d.Edges.Where(e => e.Type == Branch && e.From == n.Id).Select(e => d.Node(e.To)).OfType<DiagramNode>().OrderBy(c => c.Y).ThenBy(c => c.X);

    /// <summary>The colour of the main topic a shape belongs to (its own "line" colour wins).</summary>
    public static string ColourOf(Diagram d, DiagramNode n)
    {
        var seen = new HashSet<string>();
        for (var p = n; p is not null && seen.Add(p.Id); p = ParentOf(d, p))
        {
            if (p.Prop("line") is { Length: > 0 } own)
                return own;
            if (ParentOf(d, p) is { Type: Central } root)
            {
                var index = ChildrenOf(d, root).ToList().FindIndex(c => c.Id == p.Id);
                return Colours[Math.Max(0, index) % Colours.Length];
            }
        }
        return "";
    }

    /// <summary>A new idea hanging from <paramref name="parent"/>, placed after its last child on the side it grows to.</summary>
    public IDiagramOperation AddChild(Diagram d, DiagramNode parent)
    {
        var type = parent.Type == Central ? Topic : SubTopic;
        var children = ChildrenOf(d, parent).ToList();
        var grandParent = ParentOf(d, parent);
        var side = grandParent is null ? (children.Count % 2 == 0 ? 1 : -1) : Math.Sign(parent.Bounds.Center.X - grandParent.Bounds.Center.X) is var s && s != 0 ? s : 1;
        var sameSide = children.Where(c => Math.Sign(c.Bounds.Center.X - parent.Bounds.Center.X) == side).ToList();
        var t = this.NodeType(type)!;
        var x = parent.Bounds.Center.X + side * (parent.Width / 2 + 70 + t.Width / 2);
        var y = sameSide.Count == 0 ? parent.Bounds.Center.Y : sameSide.Max(c => c.Bounds.Bottom) + 16 + t.Height / 2;
        var node = this.NewNode(d, type, new DPoint(x, y));
        var edge = new DiagramEdge { Id = DiagramIds.NewIn(d), Type = Branch, From = parent.Id, To = node.Id };
        while (edge.Id == node.Id)
            edge.Id = DiagramIds.New();
        return new AddElements([node], [edge], L.T("Add Idea"));
    }

    /// <summary>
    /// Lays the map out as a two-sided tree: main topics alternate right and left of the central idea, each branch
    /// gets the height its ideas need, so nothing overlaps.
    /// </summary>
    public static IDiagramOperation Arrange(Diagram d, double gapX = 60, double gapY = 14)
    {
        var ops = new List<IDiagramOperation>();
        foreach (var root in d.Nodes.Where(n => n.Type == Central))
        {
            // Each idea once (a map drawn with a loop still arranges), heights bottom-up.
            var heights = new Dictionary<string, double>();
            var tree = new Dictionary<string, List<DiagramNode>>();
            var seen = new HashSet<string> { root.Id };
            double Measure(DiagramNode n)
            {
                var children = ChildrenOf(d, n).Where(c => seen.Add(c.Id)).ToList();
                tree[n.Id] = children;
                var total = children.Sum(Measure) + Math.Max(0, children.Count - 1) * gapY;
                return heights[n.Id] = Math.Max(n.Height, total);
            }
            Measure(root);
            void Place(DiagramNode n, double x, double top, int side)
            {
                var h = heights[n.Id];
                var left = side > 0 ? x : x - n.Width;
                ops.Add(new SetBounds(n.Id, new DRect(left, top + (h - n.Height) / 2, n.Width, n.Height)));
                var children = tree[n.Id];
                var y = top + (h - (children.Sum(c => heights[c.Id]) + Math.Max(0, children.Count - 1) * gapY)) / 2;
                foreach (var c in children)
                {
                    Place(c, side > 0 ? left + n.Width + gapX : left - gapX, y, side);
                    y += heights[c.Id] + gapY;
                }
            }
            var centre = root.Bounds.Center;
            var topics = tree[root.Id];
            foreach (var side in new[] { 1, -1 })
            {
                var mine = topics.Where((_, i) => (i % 2 == 0) == (side > 0)).ToList();
                var y = centre.Y - (mine.Sum(t => heights[t.Id]) + Math.Max(0, mine.Count - 1) * gapY * 2) / 2;
                foreach (var t in mine)
                {
                    Place(t, centre.X + side * (root.Width / 2 + gapX * 1.3), y, side);
                    y += heights[t.Id] + gapY * 2;
                }
            }
        }
        return new Batch(L.T("Arrange"), ops);
    }

    private static IEnumerable<DiagramProblem> Orphans(Diagram d)
    {
        if (d.Nodes.Count(n => n.Type == Central) > 1)
            yield return new DiagramProblem(ProblemSeverity.Info, L.T("A mind map usually has one central idea."), d.Nodes.Where(n => n.Type == Central).Select(n => n.Id).ToList());
        foreach (var n in d.Nodes.Where(n => n.Type is Topic or SubTopic && ParentOf(d, n) is null))
            yield return new DiagramProblem(ProblemSeverity.Info, L.T($"\"{n.Text}\" isn't on a branch."), [n.Id]);
    }
}

/// <summary>The map as an indented Markdown list.</summary>
public sealed class MindMapOutline : IDiagramTextExporter
{
    public string Name => L.T("Outline (Markdown)");
    public string Extension => "md";

    public string Export(Diagram d)
    {
        var w = new StringBuilder();
        var seen = new HashSet<string>();
        void Write(DiagramNode n, int depth)
        {
            if (!seen.Add(n.Id))
                return;
            w.Append(depth == 0 ? "# " : new string(' ', (depth - 1) * 2) + "- ").Append(Text.Line(n.Text)).Append(depth == 0 ? "\n\n" : "\n");
            foreach (var c in MindMapModel.ChildrenOf(d, n))
                Write(c, depth + 1);
        }
        foreach (var root in d.Nodes.Where(n => n.Type == MindMapModel.Central || (n.Type != MindMapModel.Note && MindMapModel.ParentOf(d, n) is null)))
            Write(root, 0);
        return w.ToString();
    }
}

/// <summary>Mermaid's mindmap.</summary>
public sealed class MindMapMermaid : IDiagramTextExporter
{
    public string Name => "Mermaid";
    public string Extension => "mmd";

    public string Export(Diagram d)
    {
        var w = new StringBuilder("mindmap\n");
        var seen = new HashSet<string>();
        void Write(DiagramNode n, int depth)
        {
            if (!seen.Add(n.Id))
                return;
            var t = Text.Line(n.Text).Replace("(", "").Replace(")", "").Replace("[", "").Replace("]", "");
            w.Append(new string(' ', 2 + depth * 2)).Append(depth == 0 ? $"root(({t}))" : n.Type == MindMapModel.Topic ? $"({t})" : t).Append('\n');
            foreach (var c in MindMapModel.ChildrenOf(d, n))
                Write(c, depth + 1);
        }
        if (d.Nodes.FirstOrDefault(n => n.Type == MindMapModel.Central) is { } root)
            Write(root, 0);
        return w.ToString();
    }
}

internal static class Text
{
    public static string Quote(string s) => s.Replace("\"", "'").Replace("\r", "").Replace("\n", "<br/>");

    public static string Line(string s) => s.Replace("\r", "").Replace("\n", " ");
}
