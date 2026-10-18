using Avalonia.Media;
using DevHub.Diagrams.Business.Core;
using DevHub.Diagrams.Core;
using DevHub.Diagrams.Rendering;
using DevHub.Sdk.Core.Localization;
using Icons = DevHub.Controls.IconKind;

namespace DevHub.Diagrams.Business;

/// <summary>Flowcharts.</summary>
public sealed class FlowchartKind : DiagramKindBase
{
    public override IDiagramKindModel Model { get; } = new FlowchartModel();
    public override string IconKind => KindIcon("flowchart");
    public override int Order => 80;

    protected override EdgeLook Look(DiagramEdge e) => e.Type == FlowchartModel.NoteLink ? new(StrokeDash.Dashed) : new(End: LineEnd.Filled);

    public override IReadOnlyList<PropertyField> EdgeFields(DiagramEdge e) => e.Type == FlowchartModel.Flow
        ?
        [
            new(L.T("Label"), "@text", Hint: L.T("Out of a decision: yes, no …")),
            new(L.T("Route"), "@routing", FieldKind.Choice) { Choices = [new("straight", L.T("Straight")), new("orthogonal", L.T("Right angles"))] },
            .. LineStyleFields,
        ]
        : base.EdgeFields(e);

    public override void DrawNode(IDiagramSurface s, Diagram d, DiagramNode n)
    {
        var r = n.Bounds;
        var pen = PenOf(s, n);
        var fill = FillOf(s, n);
        var text = TextOf(n, new DText(12));
        switch (n.Type)
        {
            case FlowchartModel.Terminator:
                s.Rect(r, fill, pen, r.Height / 2);
                s.Text(n.Text, r.Inflate(-r.Height / 3), text);
                break;
            case FlowchartModel.InputOutput:
            {
                var k = Math.Min(20, r.Width * 0.18);
                s.Polygon([new(r.Left + k, r.Top), new(r.Right, r.Top), new(r.Right - k, r.Bottom), new(r.Left, r.Bottom)], fill, pen);
                s.Text(n.Text, r.Inflate(-k), text);
                break;
            }
            case FlowchartModel.ManualInput:
            {
                var k = Math.Min(16, r.Height * 0.35);
                s.Polygon([new(r.Left, r.Top + k), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)], fill, pen);
                s.Text(n.Text, new DRect(r.X + 6, r.Y + k / 2, r.Width - 12, r.Height - k / 2), text);
                break;
            }
            case FlowchartModel.Predefined:
                s.Rect(r, fill, pen);
                s.Line(new DPoint(r.X + 10, r.Top), new DPoint(r.X + 10, r.Bottom), pen);
                s.Line(new DPoint(r.Right - 10, r.Top), new DPoint(r.Right - 10, r.Bottom), pen);
                s.Text(n.Text, r.Inflate(-14), text);
                break;
            case FlowchartModel.Document:
                Document(s, n);
                break;
            case FlowchartModel.Database:
                Cylinder(s, n);
                break;
            case FlowchartModel.Note:
                Note(s, n);
                break;
            default:
                base.DrawNode(s, d, n);
                break;
        }
    }
}

/// <summary>BPMN business processes.</summary>
public sealed class BpmnKind : DiagramKindBase
{
    private const double Band = 30;

    public override IDiagramKindModel Model { get; } = new BpmnModel();
    public override string IconKind => KindIcon("bpmn");
    public override int Order => 81;

    protected override EdgeLook Look(DiagramEdge e) => e.Type switch
    {
        BpmnModel.MessageFlow => new(StrokeDash.Dashed, Start: LineEnd.Circle, End: LineEnd.Hollow),
        BpmnModel.Association => new(StrokeDash.Dotted),
        _ => new(End: LineEnd.Filled),
    };

    private static IReadOnlyList<FieldChoice> Triggers =>
    [
        new("none", L.T("None")), new("message", L.T("Message")), new("timer", L.T("Timer")), new("error", L.T("Error")),
        new("signal", L.T("Signal")), new("conditional", L.T("Condition")), new("terminate", L.T("Terminate (end only)")),
    ];

    public override IReadOnlyList<PropertyField> NodeFields(DiagramNode n) => n.Type switch
    {
        BpmnModel.StartEvent or BpmnModel.IntermediateEvent or BpmnModel.EndEvent =>
        [
            new(L.T("Name"), "@text"),
            new(L.T("Trigger"), "trigger", FieldKind.Choice) { Choices = Triggers, Default = n.Type == BpmnModel.IntermediateEvent ? "timer" : "none" },
            .. n.Type == BpmnModel.IntermediateEvent ? [new PropertyField(L.T("Throws (filled marker)"), "throwing", FieldKind.Toggle)] : Array.Empty<PropertyField>(),
            .. StyleFields,
        ],
        BpmnModel.Task =>
        [
            new(L.T("Name"), "@text"),
            new(L.T("Task type"), "taskType", FieldKind.Choice)
            {
                Choices =
                [
                    new("none", L.T("Plain")), new("user", L.T("User (a person, with software)")), new("manual", L.T("Manual (by hand)")),
                    new("service", L.T("Service (automatic)")), new("send", L.T("Send")), new("receive", L.T("Receive")),
                    new("script", L.T("Script")), new("businessRule", L.T("Business rule")),
                ],
                Default = "none",
            },
            new(L.T("Repeats (loop)"), "loop", FieldKind.Toggle),
            .. StyleFields,
        ],
        BpmnModel.Gateway =>
        [
            new(L.T("Name"), "@text"),
            new(L.T("Gateway type"), "gatewayType", FieldKind.Choice)
            {
                Choices = [new("exclusive", L.T("Exclusive (X): one way")), new("parallel", L.T("Parallel (+): all ways")),
                    new("inclusive", L.T("Inclusive (O): some ways")), new("eventBased", L.T("Event-based: the first event wins"))],
                Default = "exclusive",
            },
            .. StyleFields,
        ],
        _ => base.NodeFields(n),
    };

    public override IReadOnlyList<PropertyField> EdgeFields(DiagramEdge e) => e.Type == BpmnModel.SequenceFlow
        ?
        [
            new(L.T("Condition"), "@text"),
            new(L.T("Default flow (slash)"), "default", FieldKind.Toggle),
            new(L.T("Route"), "@routing", FieldKind.Choice) { Choices = [new("straight", L.T("Straight")), new("orthogonal", L.T("Right angles"))] },
            .. LineStyleFields,
        ]
        : base.EdgeFields(e);

    public override void DrawNode(IDiagramSurface s, Diagram d, DiagramNode n)
    {
        var r = n.Bounds;
        var pen = PenOf(s, n);
        switch (n.Type)
        {
            case BpmnModel.Pool or BpmnModel.Lane:
            {
                s.Rect(r, FillOf(s, n, n.Type == BpmnModel.Pool ? s.Style.Background : s.Style.ContainerFill), pen);
                s.Line(new DPoint(r.X + Band, r.Y), new DPoint(r.X + Band, r.Bottom), pen);
                var band = new DRect(r.X, r.Y, Band, r.Height);
                using (s.Rotate(band.Center, -90))
                    s.Text(n.Text, new DRect(band.Center.X - r.Height / 2 + 6, band.Center.Y - Band / 2, r.Height - 12, Band),
                        TextOf(n, new DText(12, Bold: n.Type == BpmnModel.Pool)), wrap: false);
                break;
            }
            case BpmnModel.StartEvent or BpmnModel.IntermediateEvent or BpmnModel.EndEvent:
            {
                var fill = FillOf(s, n, s.Style.Background);
                var thick = n.Type == BpmnModel.EndEvent ? 3.2 : 1.4;
                s.Ellipse(r, fill, pen with { Thickness = thick });
                if (n.Type == BpmnModel.IntermediateEvent)
                    s.Ellipse(r.Inflate(-3.5), null, pen);
                var trigger = n.Prop("trigger", n.Type == BpmnModel.IntermediateEvent ? "timer" : "none");
                var filled = n.Type == BpmnModel.EndEvent || n.Flag("throwing");
                Marker(s, r.Inflate(-r.Width * 0.24), trigger, filled, pen);
                Caption(s, n, r);
                break;
            }
            case BpmnModel.Task or BpmnModel.SubProcess:
            {
                s.Rect(r, FillOf(s, n), pen, 10);
                s.Text(n.Text, r.Inflate(-10), TextOf(n, new DText(12)));
                if (n.Type == BpmnModel.SubProcess)
                {
                    var box = new DRect(r.Center.X - 7, r.Bottom - 17, 14, 14);
                    s.Rect(box, null, pen);
                    s.Line(new DPoint(box.Center.X, box.Y + 3), new DPoint(box.Center.X, box.Bottom - 3), pen);
                    s.Line(new DPoint(box.X + 3, box.Center.Y), new DPoint(box.Right - 3, box.Center.Y), pen);
                }
                else
                    TaskIcon(s, new DRect(r.X + 6, r.Y + 6, 16, 14), n.Prop("taskType", "none"), pen);
                if (n.Flag("loop"))
                {
                    var c = new DPoint(r.Center.X + (n.Type == BpmnModel.SubProcess ? 18 : 0), r.Bottom - 10);
                    s.Curve([c + new DPoint(4, 3), c + new DPoint(5, -3), c + new DPoint(0, -6), c + new DPoint(-5, -3), c + new DPoint(-4, 3)], pen);
                    s.Line(c + new DPoint(-4, 3), c + new DPoint(-7, 1), pen);
                }
                break;
            }
            case BpmnModel.Gateway:
            {
                s.Polygon(Diamond(r), FillOf(s, n, s.Style.Background), pen);
                var m = r.Inflate(-r.Width * 0.3);
                var bold = pen with { Thickness = 3 };
                switch (n.Prop("gatewayType", "exclusive"))
                {
                    case "parallel":
                        s.Line(new DPoint(m.Center.X, m.Top), new DPoint(m.Center.X, m.Bottom), bold);
                        s.Line(new DPoint(m.Left, m.Center.Y), new DPoint(m.Right, m.Center.Y), bold);
                        break;
                    case "inclusive":
                        s.Ellipse(m.Inflate(1), null, pen with { Thickness = 2.4 });
                        break;
                    case "eventBased":
                        s.Ellipse(m.Inflate(2), null, pen);
                        s.Ellipse(m.Inflate(-1), null, pen);
                        Marker(s, m.Inflate(-2.5), "pentagon", false, pen);
                        break;
                    default:
                        var x = m.Inflate(-1.5);
                        s.Line(new DPoint(x.Left, x.Top), new DPoint(x.Right, x.Bottom), bold);
                        s.Line(new DPoint(x.Right, x.Top), new DPoint(x.Left, x.Bottom), bold);
                        break;
                }
                Caption(s, n, r);
                break;
            }
            case BpmnModel.DataObject:
            {
                var fold = Math.Min(12, r.Width * 0.3);
                s.Polygon([new(r.Left, r.Top), new(r.Right - fold, r.Top), new(r.Right, r.Top + fold), new(r.Right, r.Bottom), new(r.Left, r.Bottom)],
                    FillOf(s, n, s.Style.Background), pen);
                s.Polyline([new(r.Right - fold, r.Top), new(r.Right - fold, r.Top + fold), new(r.Right, r.Top + fold)], pen);
                Caption(s, n, r);
                break;
            }
            case BpmnModel.DataStore:
            {
                var copy = new DiagramNode { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, Text = "", Props = n.Props };
                Cylinder(s, copy);
                Caption(s, n, r);
                break;
            }
            case BpmnModel.Annotation:
                s.Polyline([new(r.Left + 12, r.Top), new(r.Left, r.Top), new(r.Left, r.Bottom), new(r.Left + 12, r.Bottom)], pen);
                s.Text(n.Text, new DRect(r.X + 8, r.Y + 2, r.Width - 10, r.Height - 4), TextOf(n, new DText(11.5, Align: TextAlign.Left)));
                break;
            default:
                base.DrawNode(s, d, n);
                break;
        }
    }

    /// <summary>The name under a small shape (events, gateways, data).</summary>
    private static void Caption(IDiagramSurface s, DiagramNode n, DRect r)
    {
        if (n.Text.Length == 0)
            return;
        var style = TextOf(n, new DText(11));
        var (w, h) = s.Measure(n.Text, style, 140);
        s.Text(n.Text, new DRect(r.Center.X - w / 2 - 2, r.Bottom + 3, w + 4, h), style);
    }

    /// <summary>An event's trigger: envelope, clock, lightning, triangle, page, filled dot.</summary>
    private static void Marker(IDiagramSurface s, DRect m, string trigger, bool filled, DPen pen)
    {
        var fill = filled ? pen.Color : (Color?)null;
        var thin = pen with { Thickness = 1.1 };
        switch (trigger)
        {
            case "message":
            {
                var e = new DRect(m.X, m.Y + m.Height * 0.2, m.Width, m.Height * 0.6);
                s.Rect(e, fill, thin);
                s.Polyline([new(e.Left, e.Top), new(e.Center.X, e.Center.Y + 1), new(e.Right, e.Top)], filled ? thin with { Color = s.Style.Background } : thin);
                break;
            }
            case "timer":
                s.Ellipse(m, s.Style.Background, thin);
                s.Line(m.Center, new DPoint(m.Center.X, m.Top + 2), thin);
                s.Line(m.Center, new DPoint(m.Right - 3, m.Center.Y), thin);
                break;
            case "error":
                s.Polygon([new(m.Left, m.Bottom), new(m.Left + m.Width * 0.3, m.Top + m.Height * 0.2), new(m.Center.X + 1, m.Center.Y),
                    new(m.Right, m.Top), new(m.Right - m.Width * 0.3, m.Bottom - m.Height * 0.2), new(m.Center.X - 1, m.Center.Y + 2)], fill, thin);
                break;
            case "signal":
                s.Polygon([new(m.Center.X, m.Top), new(m.Right, m.Bottom - 1), new(m.Left, m.Bottom - 1)], fill, thin);
                break;
            case "conditional":
                s.Rect(m.Inflate(-1), fill, thin);
                for (var i = 1; i <= 3; i++)
                    s.Line(new DPoint(m.X + 3, m.Y + i * m.Height / 4), new DPoint(m.Right - 3, m.Y + i * m.Height / 4), thin);
                break;
            case "terminate":
                s.Ellipse(m.Inflate(1), pen.Color, null);
                break;
            case "pentagon":
                var c = m.Center;
                var pts = Enumerable.Range(0, 5).Select(i => new DPoint(c.X + m.Width / 2 * Math.Sin(i * 2 * Math.PI / 5), c.Y - m.Height / 2 * Math.Cos(i * 2 * Math.PI / 5))).ToList();
                s.Polygon(pts, null, thin);
                break;
        }
    }

    private static void TaskIcon(IDiagramSurface s, DRect r, string type, DPen pen)
    {
        var thin = pen with { Thickness = 1 };
        switch (type)
        {
            case "user":
                s.Ellipse(new DRect(r.Center.X - 3.5, r.Y, 7, 7), null, thin);
                s.Polyline([new(r.X + 2, r.Bottom), new(r.X + 2, r.Y + 10), new(r.Right - 2, r.Y + 10), new(r.Right - 2, r.Bottom)], thin);
                break;
            case "manual":
                s.Polyline([new(r.X, r.Y + 6), new(r.Right - 2, r.Y + 6), new(r.Right - 2, r.Bottom), new(r.X, r.Bottom), new(r.X, r.Y + 6)], thin);
                s.Line(new DPoint(r.X + 4, r.Y + 9), new DPoint(r.Right - 4, r.Y + 9), thin);
                break;
            case "service":
                s.Ellipse(r with { Width = r.Height }, null, thin);
                s.Ellipse(new DRect(r.X + r.Height / 2 - 2.5, r.Center.Y - 2.5, 5, 5), null, thin);
                break;
            case "send" or "receive":
            {
                var e = r with { Height = r.Height - 3, Y = r.Y + 2 };
                s.Rect(e, type == "send" ? pen.Color : null, thin);
                s.Polyline([new(e.Left, e.Top), new(e.Center.X, e.Center.Y), new(e.Right, e.Top)], type == "send" ? thin with { Color = s.Style.Background } : thin);
                break;
            }
            case "script":
                s.Rect(r with { Width = r.Height }, null, thin);
                for (var i = 1; i <= 3; i++)
                    s.Line(new DPoint(r.X + 3, r.Y + i * r.Height / 4), new DPoint(r.X + r.Height - 3, r.Y + i * r.Height / 4), thin);
                break;
            case "businessRule":
                s.Rect(r, null, thin);
                s.Rect(new DRect(r.X, r.Y, r.Width, 4), pen.Color, null);
                s.Line(new DPoint(r.X + 5, r.Y + 4), new DPoint(r.X + 5, r.Bottom), thin);
                s.Line(new DPoint(r.X, r.Y + 9), new DPoint(r.Right, r.Y + 9), thin);
                break;
        }
    }

    public override void DrawEdge(IDiagramSurface s, Diagram d, DiagramEdge e, IReadOnlyList<DPoint> path)
    {
        base.DrawEdge(s, d, e, path);
        if (e.Type == BpmnModel.SequenceFlow && e.Flag("default") && path.Count >= 2)
        {
            // The default flow's slash, near its start.
            var a = path[0];
            var b = path[1];
            var len = Math.Max(0.01, Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)));
            var u = new DPoint((b.X - a.X) / len, (b.Y - a.Y) / len);
            var v = new DPoint(-u.Y, u.X);
            var c = a + u * 12;
            s.Line(c - u * 4 + v * 6, c + u * 4 - v * 6, PenOf(s, e));
        }
    }
}

/// <summary>Mind maps: coloured curved branches, Add Idea and Arrange.</summary>
public sealed class MindMapKind : DiagramKindBase
{
    public override IDiagramKindModel Model { get; } = new MindMapModel();
    public override string IconKind => KindIcon("mindmap");
    public override int Order => 82;

    public override IReadOnlyList<KindCommand> Commands =>
    [
        new(L.T("Add Idea"), Icons.Add, editor =>
        {
            var model = (MindMapModel)Model;
            var parent = editor.Selection.Select(editor.Diagram.Node).FirstOrDefault(n => n is { Type: not MindMapModel.Note })
                         ?? editor.Diagram.Nodes.FirstOrDefault(n => n.Type == MindMapModel.Central);
            if (parent is null)
                return Task.CompletedTask;
            var op = model.AddChild(editor.Diagram, parent);
            if (editor.Do(op) && op is AddElements add)
                editor.Select([add.Nodes[0].Id]);
            return Task.CompletedTask;
        })
        { Description = L.T("Adds an idea to the selected one (or to the central idea).") },
        new(L.T("Arrange Map"), Icons.Refresh, editor =>
        {
            editor.Do(MindMapModel.Arrange(editor.Diagram));
            return Task.CompletedTask;
        })
        { Description = L.T("Spreads the ideas as a tree on both sides of the central idea.") },
    ];

    public override void DrawNode(IDiagramSurface s, Diagram d, DiagramNode n)
    {
        var r = n.Bounds;
        var branch = TryColor(MindMapModel.ColourOf(d, n)) ?? s.Style.Accent;
        switch (n.Type)
        {
            case MindMapModel.Central:
                s.Ellipse(r, TryColor(n.Prop("fill")) ?? s.Style.Accent, TryColor(n.Prop("line")) is { } line ? new DPen(line, 1.5) : null);
                s.Text(n.Text, r.Inflate(-r.Width * 0.12), TextOf(n, new DText(15, Bold: true, Color: Colors.White)));
                break;
            case MindMapModel.Topic:
                s.Rect(r, FillOf(s, n), new DPen(branch, 2), r.Height / 2);
                s.Text(n.Text, r.Inflate(-8), TextOf(n, new DText(13, Bold: true)));
                break;
            case MindMapModel.SubTopic:
                if (TryColor(n.Prop("fill")) is { } fill)
                    s.Rect(r, fill, null, 4);
                s.Line(new DPoint(r.Left, r.Bottom), new DPoint(r.Right, r.Bottom), new DPen(branch, 1.6));
                s.Text(n.Text, r.Inflate(-3), TextOf(n, new DText(12)));
                break;
            case MindMapModel.Note:
                Note(s, n);
                break;
            default:
                base.DrawNode(s, d, n);
                break;
        }
    }

    public override void DrawEdge(IDiagramSurface s, Diagram d, DiagramEdge e, IReadOnlyList<DPoint> path)
    {
        if (d.Node(e.From) is not { } a || d.Node(e.To) is not { } b || Branch(d, e) is not { } points)
            return;
        var colour = TryColor(e.Prop("line")) ?? TryColor(MindMapModel.ColourOf(d, b)) ?? s.Style.Line;
        var width = double.TryParse(e.Prop("width"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)
            ? w : a.Type == MindMapModel.Central ? 3.2 : 1.8;
        var pen = new DPen(colour, width, DashOf(e, StrokeDash.Solid));
        s.Polyline(points, pen);
        DrawEnds(s, e, points, LineEnd.None, LineEnd.None, colour);
        if (e.Text.Length > 0)
            Label(s, e.Text, LabelAnchor(d, e, path), 10.5, s.Style.MutedText, edge: e);
    }

    /// <summary>The middle of the branch's curve, plus where its label was dragged to.</summary>
    public override DPoint LabelAnchor(Diagram d, DiagramEdge e, IReadOnlyList<DPoint> path) =>
        (Branch(d, e) is { } points ? points[10] : Routing.LabelPoint(path, e.LabelAt)) + Routing.LabelOffset(e);

    /// <summary>
    /// A branch from the parent's side to the child's near side (sub-topics: the end of their underline), as an S-curve
    /// of 21 points; null when a shape is missing.
    /// </summary>
    private static List<DPoint>? Branch(Diagram d, DiagramEdge e)
    {
        if (d.Node(e.From) is not { } a || d.Node(e.To) is not { } b)
            return null;
        var right = b.Bounds.Center.X >= a.Bounds.Center.X;
        var start = a.Type == MindMapModel.Central ? a.Bounds.Center + new DPoint(right ? a.Width * 0.42 : -a.Width * 0.42, 0)
            : new DPoint(right ? a.Bounds.Right : a.Bounds.Left, a.Type == MindMapModel.SubTopic ? a.Bounds.Bottom : a.Bounds.Center.Y);
        var end = new DPoint(right ? b.Bounds.Left : b.Bounds.Right, b.Type == MindMapModel.SubTopic ? b.Bounds.Bottom : b.Bounds.Center.Y);
        var dx = (end.X - start.X) * 0.5;
        var c1 = start + new DPoint(dx, 0);
        var c2 = end - new DPoint(dx, 0);
        var points = new List<DPoint>();
        for (var i = 0; i <= 20; i++)
        {
            var t = i / 20.0;
            var u = 1 - t;
            points.Add(start * (u * u * u) + c1 * (3 * u * u * t) + c2 * (3 * u * t * t) + end * (t * t * t));
        }
        return points;
    }

    public override DRect? Fit(DiagramNode n, IDiagramSurface m)
    {
        if (n.Type is not (MindMapModel.Topic or MindMapModel.SubTopic))
            return null;
        var style = n.Type == MindMapModel.Topic ? new DText(13, Bold: true) : new DText(12);
        var (w, h) = m.Measure(n.Text, style, 260);
        var fit = n.Bounds with { Width = Math.Ceiling(Math.Max(60, w + (n.Type == MindMapModel.Topic ? 28 : 10))), Height = Math.Ceiling(Math.Max(n.Type == MindMapModel.Topic ? 40 : 26, h + 10)) };
        return fit == n.Bounds ? null : fit;
    }
}
