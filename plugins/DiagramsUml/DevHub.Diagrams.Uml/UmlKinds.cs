using Avalonia.Media;
using Icons = DevHub.Controls.IconKind;
using DevHub.Diagrams.Core;
using DevHub.Diagrams.Rendering;
using DevHub.Diagrams.Uml.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Uml;

/// <summary>Use case diagrams, drawn like the course and Visual Paradigm.</summary>
public sealed class UseCaseKind : DiagramKindBase
{
    public override IDiagramKindModel Model { get; } = new UseCaseModel();
    public override string IconKind => KindIcon("usecase");
    public override int Order => 10;

    protected override EdgeLook Look(DiagramEdge e) => e.Type switch
    {
        UseCaseModel.Include or UseCaseModel.Extend => new(StrokeDash.Dashed, End: LineEnd.Open),
        UseCaseModel.Generalization => new(End: LineEnd.Hollow),
        UseCaseModel.NoteLink => new(StrokeDash.Dashed),
        _ => new(),
    };

    public override IReadOnlyList<PropertyField> NodeFields(DiagramNode n) => n.Type switch
    {
        UseCaseModel.Actor => [new(L.T("Name"), "@text"), new(L.T("Stereotype"), "stereotype", Hint: L.T("e.g. Service, System (empty: a person)")), .. StyleFields],
        UseCaseModel.UseCase => [new(L.T("Name"), "@text"),
            new(L.T("Extension points"), "extensionPoints", FieldKind.MultilineText, L.T("One per line: where «extend» use cases plug in.")), .. StyleFields],
        UseCaseModel.Boundary => [new(L.T("System name"), "@text"), .. StyleFields],
        _ => base.NodeFields(n),
    };

    public override IReadOnlyList<PropertyField> EdgeFields(DiagramEdge e) => e.Type == UseCaseModel.Extend
        ? [new(L.T("Label"), "@text"), new(L.T("Condition"), "condition", Hint: L.T("When it extends (shown as [condition])")), .. LineStyleFields]
        : base.EdgeFields(e);

    public override IReadOnlyList<PropertyField> DiagramFields =>
        [new(L.T("Title"), "@title"), new(L.T("Course colours (blue shapes, yellow system)"), "courseColours", FieldKind.Toggle)];

    public override void DrawNode(IDiagramSurface s, Diagram d, DiagramNode n)
    {
        var r = n.Bounds;
        switch (n.Type)
        {
            case UseCaseModel.Actor when n.Prop("stereotype") is { Length: > 0 } stereotype:
            {
                // «Service» Authentication: a small figure on the left, the stereotype and name beside it.
                var pen = PenOf(s, n);
                var figure = new DiagramNode { X = r.X, Y = r.Y, Width = Math.Min(28, r.Width * 0.3), Height = Math.Min(44, r.Height), Text = "" };
                StickFigure(s, figure, pen);
                var text = new DRect(r.X + figure.Width + 6, r.Y, r.Width - figure.Width - 6, 44);
                s.Text(Guillemets(stereotype), text with { Height = 22 }, new DText(11, Align: TextAlign.Left, Color: s.Style.MutedText), wrap: false);
                s.Text(n.Text, text with { Y = text.Y + 20, Height = 22 }, TextOf(n, new DText(12, Bold: true, Align: TextAlign.Left)), wrap: false);
                break;
            }
            case UseCaseModel.Actor:
                StickFigure(s, n, PenOf(s, n));
                break;
            case UseCaseModel.UseCase:
            {
                var fill = FillOf(s, n);
                var pen = PenOf(s, n);
                s.Ellipse(r, fill, pen);
                var points = ClassMember.Lines(n.Prop("extensionPoints")).ToList();
                if (points.Count == 0)
                {
                    s.Text(n.Text, r.Inflate(-r.Width * 0.12), TextOf(n, new DText(12, Bold: true)));
                    break;
                }
                // Name above a line across the ellipse, "extension points" and the list below.
                var middle = r.Y + r.Height * 0.42;
                var half = Math.Sqrt(Math.Max(0, 1 - Math.Pow((middle - r.Center.Y) / (r.Height / 2), 2))) * r.Width / 2;
                s.Line(new DPoint(r.Center.X - half, middle), new DPoint(r.Center.X + half, middle), pen);
                s.Text(n.Text, new DRect(r.X + r.Width * 0.12, r.Y + 4, r.Width * 0.76, middle - r.Y - 4), TextOf(n, new DText(12, Bold: true)));
                s.Text(L.T("extension points") + "\n" + string.Join("\n", points),
                    new DRect(r.X + r.Width * 0.15, middle + 2, r.Width * 0.7, r.Bottom - middle - 8), new DText(10.5, Vertical: TextVertical.Top));
                break;
            }
            case UseCaseModel.Boundary:
                s.Rect(r, FillOf(s, n, s.Style.ContainerFill), PenOf(s, n));
                s.Text(n.Text, new DRect(r.X, r.Y + 8, r.Width, 20), TextOf(n, new DText(13, Bold: true)), wrap: false);
                break;
            case UseCaseModel.Note:
                Note(s, n);
                break;
            default:
                base.DrawNode(s, d, n);
                break;
        }
    }

    public override void DrawEdge(IDiagramSurface s, Diagram d, DiagramEdge e, IReadOnlyList<DPoint> path)
    {
        base.DrawEdge(s, d, e, path);
        if (e.Type == UseCaseModel.Extend && e.Prop("condition") is { Length: > 0 } condition && path.Count >= 2)
            Label(s, $"[{condition.Trim('[', ']')}]", Routing.LabelPoint(path, e.LabelAt) + new DPoint(0, 16), 10.5, s.Style.MutedText);
    }

    public override DRect? Fit(DiagramNode n, IDiagramSurface m)
    {
        if (n.Type != UseCaseModel.UseCase)
            return null;
        var points = ClassMember.Lines(n.Prop("extensionPoints")).Count();
        var (w, h) = m.Measure(n.Text, new DText(12, Bold: true), 200);
        var height = Math.Max(n.Height, h + 30 + (points > 0 ? 18 + points * 15 : 0));
        var width = Math.Max(n.Width, Math.Min(260, w * 1.35 + 30));
        return width == n.Width && height == n.Height ? null : n.Bounds with { Width = width, Height = height };
    }
}

/// <summary>Activity diagrams.</summary>
public sealed class ActivityKind : DiagramKindBase
{
    public override IDiagramKindModel Model { get; } = new ActivityModel();
    public override string IconKind => KindIcon("activity");
    public override int Order => 20;

    protected override EdgeLook Look(DiagramEdge e) => e.Type == ActivityModel.NoteLink ? new(StrokeDash.Dashed) : new(End: LineEnd.Open);

    public override IReadOnlyList<PropertyField> NodeFields(DiagramNode n) => n.Type switch
    {
        ActivityModel.Start or ActivityModel.End or ActivityModel.FlowEnd or ActivityModel.Bar => StyleFields,
        ActivityModel.Lane => [new(L.T("Role"), "@text"), new(L.T("Vertical"), "vertical", FieldKind.Toggle), .. StyleFields],
        _ => base.NodeFields(n),
    };

    public override IReadOnlyList<PropertyField> EdgeFields(DiagramEdge e) => e.Type == ActivityModel.Flow
        ? [new(L.T("Condition (guard)"), "@text", Hint: L.T("After a decision: [condition], or [otherwise]")),
           new(L.T("Route"), "@routing", FieldKind.Choice) { Choices = [new("straight", L.T("Straight")), new("orthogonal", L.T("Right angles"))] },
           .. LineStyleFields]
        : base.EdgeFields(e);

    public override IReadOnlyList<PropertyField> DiagramFields =>
        [new(L.T("Title"), "@title"), new(L.T("Course colours (blue actions, yellow lanes)"), "courseColours", FieldKind.Toggle)];

    public override void DrawNode(IDiagramSurface s, Diagram d, DiagramNode n)
    {
        var r = n.Bounds;
        var pen = PenOf(s, n);
        var ink = TryColor(n.Prop("fill")) ?? pen.Color;
        switch (n.Type)
        {
            case ActivityModel.Start:
                s.Ellipse(r, ink, null);
                break;
            case ActivityModel.End:
                s.Ellipse(r, s.Style.Background, pen);
                s.Ellipse(r.Inflate(-r.Width * 0.22), ink, null);
                break;
            case ActivityModel.FlowEnd:
                s.Ellipse(r, FillOf(s, n, s.Style.Background), pen);
                var k = r.Width * 0.5 / Math.Sqrt(2);
                s.Line(r.Center + new DPoint(-k, -k), r.Center + new DPoint(k, k), pen);
                s.Line(r.Center + new DPoint(-k, k), r.Center + new DPoint(k, -k), pen);
                break;
            case ActivityModel.Activity:
                s.Rect(r, FillOf(s, n, s.Style.ContainerFill), pen, 14);
                s.Text(n.Text, new DRect(r.X + 12, r.Y + 6, r.Width - 24, 18), TextOf(n, new DText(12, Bold: true, Align: TextAlign.Left)), wrap: false);
                break;
            case ActivityModel.Object:
                s.Rect(r, FillOf(s, n), pen);
                s.Text(n.Text, r.Inflate(-6), TextOf(n, new DText(12)));
                break;
            case ActivityModel.SendSignal:
            {
                var tip = Math.Min(18, r.Width * 0.2);
                s.Polygon([new(r.Left, r.Top), new(r.Right - tip, r.Top), new(r.Right, r.Center.Y), new(r.Right - tip, r.Bottom), new(r.Left, r.Bottom)], FillOf(s, n), pen);
                s.Text(n.Text, new DRect(r.X + 6, r.Y, r.Width - tip - 8, r.Height), TextOf(n, new DText(12)));
                break;
            }
            case ActivityModel.ReceiveSignal:
            {
                var notch = Math.Min(18, r.Width * 0.2);
                s.Polygon([new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom), new(r.Left + notch, r.Center.Y)], FillOf(s, n), pen);
                s.Text(n.Text, new DRect(r.X + notch + 4, r.Y, r.Width - notch - 8, r.Height), TextOf(n, new DText(12)));
                break;
            }
            case ActivityModel.Lane:
            {
                s.Rect(r, FillOf(s, n, s.Style.ContainerFill), pen);
                var style = TextOf(n, new DText(12, Bold: true));
                if (n.Flag("vertical"))
                {
                    s.Line(new DPoint(r.X, r.Y + 30), new DPoint(r.Right, r.Y + 30), pen);
                    s.Text(n.Text, new DRect(r.X + 4, r.Y, r.Width - 8, 30), style, wrap: false);
                }
                else
                {
                    // The role's name down the left side, turned.
                    s.Line(new DPoint(r.X + 30, r.Y), new DPoint(r.X + 30, r.Bottom), pen);
                    var band = new DRect(r.X, r.Y, 30, r.Height);
                    using (s.Rotate(band.Center, -90))
                        s.Text(n.Text, new DRect(band.Center.X - r.Height / 2, band.Center.Y - 15, r.Height, 30), style, wrap: false);
                }
                break;
            }
            case ActivityModel.Note:
                Note(s, n);
                break;
            default:
                base.DrawNode(s, d, n);
                break;
        }
    }

    public override void DrawEdge(IDiagramSurface s, Diagram d, DiagramEdge e, IReadOnlyList<DPoint> path)
    {
        if (e.Type != ActivityModel.Flow || e.Text.Trim().Length == 0)
        {
            base.DrawEdge(s, d, e, path);
            return;
        }
        // Guards always in brackets, as the notation writes them.
        var guard = e.Text.Trim();
        var shown = guard.StartsWith('[') ? guard : $"[{guard}]";
        var pen = PenOf(s, e);
        s.Polyline(path, pen);
        DrawEnds(s, e, path, LineEnd.None, LineEnd.Open, pen.Color);
        Label(s, shown, Routing.LabelPoint(path, e.LabelAt), 10.5);
    }

    public override DRect? Fit(DiagramNode n, IDiagramSurface m)
    {
        if (n.Type is not (ActivityModel.Action or ActivityModel.Object or ActivityModel.SendSignal or ActivityModel.ReceiveSignal))
            return null;
        var (_, h) = m.Measure(n.Text, new DText(12), n.Width - 16);
        var height = Math.Max(n.Type == ActivityModel.Action ? 44 : 40, h + 16);
        return height > n.Height ? n.Bounds with { Height = height } : null;
    }
}

/// <summary>Class and object diagrams: three-compartment boxes sized to their text.</summary>
public sealed class ClassKind(bool objects = false) : DiagramKindBase
{
    private const double LineHeight = 17;
    private const double Pad = 6;

    public override IDiagramKindModel Model { get; } = new ClassModel(objects);
    public override string IconKind => KindIcon(objects ? "object" : "class");
    public override int Order => objects ? 35 : 30;

    public override bool MultilineText(DiagramNode n) => false;

    // The member forms (one per kind of compartment; they hold no state).
    private static readonly ClassMemberCodec AttributeRows = new(operations: false);
    private static readonly ClassMemberCodec OperationRows = new(operations: true);
    private static readonly ObjectValueCodec ObjectValues = new();

    protected override EdgeLook Look(DiagramEdge e) => e.Type switch
    {
        ClassModel.Association => new(End: e.Prop("navigable") == "true" ? LineEnd.Open : LineEnd.None),
        ClassModel.Aggregation => new(Start: LineEnd.HollowDiamond, End: e.Prop("navigable") == "true" ? LineEnd.Open : LineEnd.None),
        ClassModel.Composition => new(Start: LineEnd.FilledDiamond, End: e.Prop("navigable") == "true" ? LineEnd.Open : LineEnd.None),
        ClassModel.Generalization => new(End: LineEnd.Hollow),
        ClassModel.Realization => new(StrokeDash.Dashed, End: LineEnd.Hollow),
        ClassModel.Dependency => new(StrokeDash.Dashed, End: LineEnd.Open),
        ClassModel.NoteLink => new(StrokeDash.Dashed),
        _ => new(),
    };

    public override IReadOnlyList<PropertyField> NodeFields(DiagramNode n) => n.Type switch
    {
        ClassModel.Class =>
        [
            new(L.T("Name"), "@text"),
            new(L.T("Abstract (name in italics)"), "abstract", FieldKind.Toggle),
            new(L.T("Stereotype"), "stereotype", Hint: L.T("e.g. entity, controller")),
            new(L.T("Attributes"), "attributes", FieldKind.Rows) { Rows = AttributeRows },
            new(L.T("Operations"), "operations", FieldKind.Rows) { Rows = OperationRows },
            new(L.T("Properties"), "properties", Hint: L.T("e.g. author=Joe, status=tested")),
            new(L.T("Show attributes"), "showAttributes", FieldKind.Toggle) { Default = "true" },
            new(L.T("Show operations"), "showOperations", FieldKind.Toggle) { Default = "true" },
            .. StyleFields,
        ],
        ClassModel.Interface or ClassModel.DataType =>
        [
            new(L.T("Name"), "@text"),
            new(L.T("Attributes"), "attributes", FieldKind.Rows) { Rows = AttributeRows },
            new(L.T("Operations"), "operations", FieldKind.Rows) { Rows = OperationRows },
            .. StyleFields,
        ],
        ClassModel.Enumeration => [new(L.T("Name"), "@text"), new(L.T("Values"), "attributes", FieldKind.MultilineText, L.T("One value per line.")), .. StyleFields],
        ClassModel.Instance =>
        [
            new(L.T("Name : Class"), "@text"),
            new(L.T("Values"), "values", FieldKind.Rows) { Rows = ObjectValues },
            .. StyleFields,
        ],
        _ => base.NodeFields(n),
    };

    public override IReadOnlyList<PropertyField> EdgeFields(DiagramEdge e) => e.Type switch
    {
        ClassModel.Association or ClassModel.Aggregation or ClassModel.Composition =>
        [
            new(L.T("Name"), "@text"),
            new(L.T("Multiplicity at the start"), "fromMultiplicity", Hint: "1, 0..1, *, 1..*"),
            new(L.T("Role at the start"), "fromRole"),
            new(L.T("Multiplicity at the end"), "toMultiplicity", Hint: "1, 0..1, *, 1..*"),
            new(L.T("Role at the end"), "toRole"),
            new(L.T("Navigable towards the end (arrow)"), "navigable", FieldKind.Toggle),
            new(L.T("Route"), "@routing", FieldKind.Choice) { Choices = [new("straight", L.T("Straight")), new("orthogonal", L.T("Right angles"))] },
            .. LineStyleFields,
        ],
        _ => base.EdgeFields(e),
    };

    private static (string? Stereotype, bool Italic) Header(DiagramNode n) => n.Type switch
    {
        ClassModel.Interface => ("interface", true),
        ClassModel.Enumeration => ("enumeration", false),
        ClassModel.DataType => ("datatype", false),
        _ => (n.Prop("stereotype") is { Length: > 0 } st ? st : null, n.Flag("abstract")),
    };

    private sealed record Row(string Text, bool Underline, bool Italic);

    private static List<Row> Rows(string text, bool literals) =>
        ClassMember.Lines(text).Select(line => literals || ClassMember.Parse(line) is not { } m
            ? new Row(line.Trim(), false, false)
            : new Row(m.Display, m.IsStatic, m.IsAbstract)).ToList();

    private static bool Shows(DiagramNode n, string key) => n.Prop(key, "true") != "false";

    // Attributes and operations follow the shape's text colour and size (its size is the name's; members a point smaller).
    private static double MemberSize(DiagramNode n) =>
        double.TryParse(n.Prop("fontSize"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var size)
        && size is >= 6 and <= 96 ? size - 1 : 12;

    private static DText MemberStyle(DiagramNode n, bool italic = false, bool underline = false) =>
        new(MemberSize(n), Italic: italic, Underline: underline, Color: TryColor(n.Prop("textColor")), Align: TextAlign.Left);

    private static double LineOf(DiagramNode n) => Math.Ceiling(MemberSize(n) * LineHeight / 12);

    private static double NameHeight(DiagramNode n) => Math.Ceiling((MemberSize(n) + 1) * 18 / 13);

    public override void DrawNode(IDiagramSurface s, Diagram d, DiagramNode n)
    {
        var r = n.Bounds;
        switch (n.Type)
        {
            case ClassModel.Note:
                Note(s, n);
                return;
            case ClassModel.Package:
                Folder(s, n, s.Style.ContainerFill);
                return;
            case ClassModel.Instance:
            {
                var pen = PenOf(s, n);
                s.Rect(r, FillOf(s, n), pen);
                var header = TextOf(n, new DText(12.5, Bold: true, Underline: true));
                var (_, hh) = s.Measure(n.Text, header);
                s.Text(n.Text, new DRect(r.X, r.Y + Pad, r.Width, hh), header, wrap: false);
                var y = r.Y + hh + Pad * 2;
                var values = ClassMember.Lines(n.Prop("values")).ToList();
                if (values.Count > 0)
                {
                    s.Line(new DPoint(r.X, y), new DPoint(r.Right, y), pen);
                    y += 4;
                    foreach (var v in values)
                    {
                        s.Text(v.Trim(), new DRect(r.X + Pad, y, r.Width - Pad * 2, LineOf(n)), MemberStyle(n), wrap: false);
                        y += LineOf(n);
                    }
                }
                return;
            }
            case ClassModel.Class or ClassModel.Interface or ClassModel.Enumeration or ClassModel.DataType:
                DrawClassifier(s, n);
                return;
            default:
                base.DrawNode(s, d, n);
                return;
        }
    }

    private void DrawClassifier(IDiagramSurface s, DiagramNode n)
    {
        var r = n.Bounds;
        var pen = PenOf(s, n);
        s.Rect(r, FillOf(s, n), pen);
        var (stereotype, italic) = Header(n);
        var y = r.Y + Pad;
        if (stereotype is not null)
        {
            s.Text(Guillemets(stereotype), new DRect(r.X, y, r.Width, 15), new DText(11, Color: s.Style.MutedText), wrap: false);
            y += 15;
        }
        var name = TextOf(n, new DText(13, Bold: true, Italic: italic));
        s.Text(n.Text, new DRect(r.X, y, r.Width, NameHeight(n)), name, wrap: false);
        y += NameHeight(n);
        if (n.Prop("properties") is { Length: > 0 } props)
        {
            s.Text("{" + props.Trim('{', '}') + "}", new DRect(r.X, y, r.Width, 15), new DText(10.5, Color: s.Style.MutedText), wrap: false);
            y += 15;
        }
        y += Pad - 2;

        var compartments = new List<List<Row>>();
        if (n.Type == ClassModel.Enumeration || Shows(n, "showAttributes"))
            compartments.Add(Rows(n.Prop("attributes"), n.Type == ClassModel.Enumeration));
        if (n.Type != ClassModel.Enumeration && Shows(n, "showOperations"))
            compartments.Add(Rows(n.Prop("operations"), false));
        foreach (var rows in compartments)
        {
            s.Line(new DPoint(r.X, y), new DPoint(r.Right, y), pen);
            y += 3;
            foreach (var row in rows)
            {
                s.Text(row.Text, new DRect(r.X + Pad, y, r.Width - Pad * 2, LineOf(n)),
                    MemberStyle(n, row.Italic, row.Underline), wrap: false);
                y += LineOf(n);
            }
            y += rows.Count == 0 ? 8 : 3;
        }
    }

    public override DRect? Fit(DiagramNode n, IDiagramSurface m)
    {
        if (n.Type is not (ClassModel.Class or ClassModel.Interface or ClassModel.Enumeration or ClassModel.DataType or ClassModel.Instance))
            return null;
        double width = m.Measure(n.Text, new DText(13, Bold: true)).Width + Pad * 4;
        double height;
        if (n.Type == ClassModel.Instance)
        {
            var values = ClassMember.Lines(n.Prop("values")).ToList();
            foreach (var v in values)
                width = Math.Max(width, m.Measure(v.Trim(), MemberStyle(n)).Width + Pad * 3);
            height = NameHeight(n) + Pad * 2 + (values.Count > 0 ? 8 + values.Count * LineOf(n) : 0);
        }
        else
        {
            var (stereotype, _) = Header(n);
            height = Pad + (stereotype is null ? 0 : 15) + NameHeight(n) + (n.Prop("properties").Length > 0 ? 15 : 0) + Pad - 2;
            var compartments = new List<List<Row>>();
            if (n.Type == ClassModel.Enumeration || Shows(n, "showAttributes"))
                compartments.Add(Rows(n.Prop("attributes"), n.Type == ClassModel.Enumeration));
            if (n.Type != ClassModel.Enumeration && Shows(n, "showOperations"))
                compartments.Add(Rows(n.Prop("operations"), false));
            foreach (var rows in compartments)
            {
                foreach (var row in rows)
                    width = Math.Max(width, m.Measure(row.Text, MemberStyle(n)).Width + Pad * 3);
                height += 3 + rows.Count * LineOf(n) + (rows.Count == 0 ? 8 : 3);
            }
        }
        var fit = n.Bounds with { Width = Math.Max(Math.Ceiling(width), 110), Height = Math.Ceiling(height) };
        return fit == n.Bounds ? null : fit;
    }

    public override void DrawEdge(IDiagramSurface s, Diagram d, DiagramEdge e, IReadOnlyList<DPoint> path)
    {
        base.DrawEdge(s, d, e, path);
        if (e.Type is ClassModel.Association or ClassModel.Aggregation or ClassModel.Composition)
        {
            EndLabel(s, e.Prop("fromMultiplicity"), path, atStart: true, side: 1);
            EndLabel(s, e.Prop("fromRole"), path, atStart: true, side: -1);
            EndLabel(s, e.Prop("toMultiplicity"), path, atStart: false, side: -1);
            EndLabel(s, e.Prop("toRole"), path, atStart: false, side: 1);
        }
    }
}

/// <summary>Sequence diagrams.</summary>
public sealed class SequenceKind : DiagramKindBase
{
    public override IDiagramKindModel Model { get; } = new SequenceModel();
    public override string IconKind => KindIcon("sequence");
    public override int Order => 40;

    public override IReadOnlyList<PropertyField> NodeFields(DiagramNode n) => n.Type switch
    {
        SequenceModel.Lifeline =>
        [
            new(L.T("Name : Class"), "@text"),
            new(L.T("Actor (a person)"), "actor", FieldKind.Toggle),
            new(L.T("Destroyed at the end (×)"), "destroyed", FieldKind.Toggle),
            .. StyleFields,
        ],
        SequenceModel.Fragment =>
        [
            new(L.T("Operator"), "@text", FieldKind.Choice)
            {
                Choices = [new("alt", L.T("alt: alternatives")), new("opt", L.T("opt: optional")), new("loop", L.T("loop: repeated")),
                    new("par", L.T("par: in parallel")), new("break", L.T("break: stops")), new("critical", L.T("critical: atomic")),
                    new("ref", L.T("ref: see another diagram")), new("neg", L.T("neg: must not happen"))],
            },
            new(L.T("Condition"), "guard", Hint: "[x > 0]"),
            new(L.T("Else condition (alt)"), "guard2", Hint: "[else]"),
            new(L.T("Else line (from the top)"), "elseAt", FieldKind.Number, L.T("Empty: halfway down")),
            .. StyleFields,
        ],
        SequenceModel.Activation => StyleFields,
        _ => base.NodeFields(n),
    };

    public override IReadOnlyList<PropertyField> EdgeFields(DiagramEdge e) => [new(L.T("Message"), "@text"), .. LineStyleFields];

    public override void DrawNode(IDiagramSurface s, Diagram d, DiagramNode n)
    {
        var r = n.Bounds;
        var pen = PenOf(s, n);
        switch (n.Type)
        {
            case SequenceModel.Lifeline:
            {
                var head = new DRect(r.X, r.Y, r.Width, SequenceModel.HeadHeight);
                if (n.Flag("actor"))
                {
                    var figure = new DiagramNode { X = r.Center.X - 15, Y = r.Y - 30, Width = 30, Height = SequenceModel.HeadHeight + 30, Text = n.Text };
                    StickFigure(s, figure, pen);
                }
                else
                {
                    s.Rect(head, FillOf(s, n), pen);
                    s.Text(n.Text, head.Inflate(-4), TextOf(n, new DText(12, Underline: n.Text.Contains(':'))));
                }
                var bottom = n.Flag("destroyed") ? r.Bottom - 10 : r.Bottom;
                s.Line(new DPoint(r.Center.X, head.Bottom), new DPoint(r.Center.X, bottom), new DPen(pen.Color, 1, StrokeDash.Dashed));
                if (n.Flag("destroyed"))
                {
                    var c = new DPoint(r.Center.X, r.Bottom - 10);
                    s.Line(c + new DPoint(-9, -9), c + new DPoint(9, 9), new DPen(pen.Color, 1.6));
                    s.Line(c + new DPoint(-9, 9), c + new DPoint(9, -9), new DPen(pen.Color, 1.6));
                }
                break;
            }
            case SequenceModel.Activation:
                s.Rect(r, FillOf(s, n), pen);
                break;
            case SequenceModel.Fragment:
            {
                s.Rect(r, TryColor(n.Prop("fill")), pen);
                var op = n.Text.Trim().Length > 0 ? n.Text.Trim() : "alt";
                var style = new DText(11.5, Bold: true, Align: TextAlign.Left);
                var (w, h) = s.Measure(op, style);
                var tag = new DRect(r.X, r.Y, w + 18, h + 6);
                s.Polygon([new(tag.Left, tag.Top), new(tag.Right, tag.Top), new(tag.Right, tag.Bottom - 6), new(tag.Right - 6, tag.Bottom), new(tag.Left, tag.Bottom)],
                    s.Style.Background, pen);
                s.Text(op, tag with { X = tag.X + 6 }, style, wrap: false);
                if (n.Prop("guard") is { Length: > 0 } guard)
                    s.Text(Bracket(guard), new DRect(tag.Right + 8, r.Y + 3, r.Width - tag.Width - 16, h), new DText(11, Align: TextAlign.Left), wrap: false);
                if (n.Prop("guard2") is { Length: > 0 } second)
                {
                    var middle = double.TryParse(n.Prop("elseAt"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at)
                        ? r.Y + at
                        : r.Y + r.Height / 2;
                    s.Line(new DPoint(r.X, middle), new DPoint(r.Right, middle), new DPen(pen.Color, 1, StrokeDash.Dashed));
                    s.Text(Bracket(second), new DRect(r.X + 8, middle + 3, r.Width - 16, h), new DText(11, Align: TextAlign.Left), wrap: false);
                }
                break;
            }
            case SequenceModel.Note:
                Note(s, n);
                break;
            default:
                base.DrawNode(s, d, n);
                break;
        }
    }

    private static string Bracket(string text) => text.Trim().StartsWith('[') ? text.Trim() : $"[{text.Trim()}]";

    public override void DrawEdge(IDiagramSurface s, Diagram d, DiagramEdge e, IReadOnlyList<DPoint> path)
    {
        if (path.Count < 2)
            return;
        var dash = e.Type is SequenceModel.Reply or SequenceModel.CreateMessage || e.Type == SequenceModel.NoteLink ? StrokeDash.Dashed : StrokeDash.Solid;
        var pen = PenOf(s, e, dash);
        s.Polyline(path, pen);
        DrawEnds(s, e, path, LineEnd.None, e.Type == SequenceModel.NoteLink ? LineEnd.None : e.Type == SequenceModel.Sync ? LineEnd.Filled : LineEnd.Open, pen.Color);
        if (e.Text.Length == 0)
            return;
        // The message's name just above its line (beside a self-message).
        var at = path.Count == 2 ? path[0] + (path[1] - path[0]) * 0.5 + new DPoint(0, -10) : path[1] + new DPoint(6, 12);
        var style = new DText(11.5, Align: path.Count == 2 ? TextAlign.Center : TextAlign.Left);
        var (w, h) = s.Measure(e.Text, style);
        s.Text(e.Text, path.Count == 2 ? new DRect(at.X - w / 2, at.Y - h / 2, w, h) : new DRect(at.X, at.Y - h / 2, w, h), style, wrap: false);
    }
}

/// <summary>State machine diagrams.</summary>
public sealed class StateKind : DiagramKindBase
{
    public override IDiagramKindModel Model { get; } = new StateModel();
    public override string IconKind => KindIcon("state");
    public override int Order => 50;

    protected override EdgeLook Look(DiagramEdge e) => e.Type == StateModel.NoteLink ? new(StrokeDash.Dashed) : new(End: LineEnd.Open);

    public override IReadOnlyList<PropertyField> NodeFields(DiagramNode n) => n.Type switch
    {
        StateModel.State or StateModel.Composite =>
        [
            new(L.T("Name"), "@text"),
            new(L.T("Activities"), "activities", FieldKind.MultilineText, L.T("One per line: entry / …, do / …, exit / …")),
            .. StyleFields,
        ],
        StateModel.Initial or StateModel.Final or StateModel.Bar or StateModel.Choice => StyleFields,
        _ => base.NodeFields(n),
    };

    public override IReadOnlyList<PropertyField> EdgeFields(DiagramEdge e) =>
        [new(L.T("Event [condition] / action"), "@text"), .. base.EdgeFields(e).Skip(1)];

    public override void DrawNode(IDiagramSurface s, Diagram d, DiagramNode n)
    {
        var r = n.Bounds;
        var pen = PenOf(s, n);
        var ink = TryColor(n.Prop("fill")) ?? pen.Color;
        switch (n.Type)
        {
            case StateModel.Initial:
                s.Ellipse(r, ink, null);
                break;
            case StateModel.Final:
                s.Ellipse(r, s.Style.Background, pen);
                s.Ellipse(r.Inflate(-r.Width * 0.22), ink, null);
                break;
            case StateModel.History:
                s.Ellipse(r, FillOf(s, n), pen);
                s.Text(n.Text.Length > 0 ? n.Text : "H", r, new DText(12, Bold: true), wrap: false);
                break;
            case StateModel.State or StateModel.Composite:
            {
                var composite = n.Type == StateModel.Composite;
                s.Rect(r, FillOf(s, n, composite ? s.Style.ContainerFill : null), pen, 12);
                var name = TextOf(n, new DText(12.5, Bold: true));
                var lines = ClassMember.Lines(n.Prop("activities")).ToList();
                if (lines.Count == 0 && !composite)
                {
                    // A plain state: its name in the middle.
                    s.Text(n.Text, r.Inflate(-6), name, wrap: true);
                    break;
                }
                s.Text(n.Text, new DRect(r.X + 6, r.Y + 5, r.Width - 12, 18), name, wrap: false);
                if (lines.Count > 0)
                {
                    s.Line(new DPoint(r.X, r.Y + 26), new DPoint(r.Right, r.Y + 26), pen);
                    s.Text(string.Join("\n", lines), new DRect(r.X + 8, r.Y + 30, r.Width - 16, r.Height - 34),
                        new DText(11.5, Align: TextAlign.Left, Vertical: TextVertical.Top));
                }
                break;
            }
            case StateModel.Note:
                Note(s, n);
                break;
            default:
                base.DrawNode(s, d, n);
                break;
        }
    }

    public override DRect? Fit(DiagramNode n, IDiagramSurface m)
    {
        if (n.Type != StateModel.State)
            return null;
        var lines = ClassMember.Lines(n.Prop("activities")).ToList();
        var width = Math.Max(n.Width, m.Measure(n.Text, new DText(12.5, Bold: true)).Width + 24);
        foreach (var l in lines)
            width = Math.Max(width, m.Measure(l, new DText(11.5)).Width + 20);
        var height = lines.Count == 0 ? n.Height : Math.Max(n.Height, 34 + lines.Count * 16 + 6);
        var fit = n.Bounds with { Width = Math.Ceiling(width), Height = Math.Ceiling(height) };
        return fit == n.Bounds ? null : fit;
    }
}

/// <summary>Component, deployment and package diagrams.</summary>
public sealed class StructureKind(string kind) : DiagramKindBase
{
    public override IDiagramKindModel Model { get; } = new StructureModel(kind);
    public override string IconKind => KindIcon(kind);
    public override int Order => kind switch { StructureModel.ComponentKind => 60, StructureModel.DeploymentKind => 61, _ => 62 };

    protected override EdgeLook Look(DiagramEdge e) => e.Type switch
    {
        StructureModel.Dependency or StructureModel.Deploy or StructureModel.Import => new(StrokeDash.Dashed, End: LineEnd.Open),
        StructureModel.Realization => new(StrokeDash.Dashed, End: LineEnd.Hollow),
        StructureModel.NoteLink => new(StrokeDash.Dashed),
        _ => new(),
    };

    public override void DrawNode(IDiagramSurface s, Diagram d, DiagramNode n)
    {
        var r = n.Bounds;
        var pen = PenOf(s, n);
        switch (n.Type)
        {
            case StructureModel.Component:
            {
                s.Rect(r, FillOf(s, n, d.Children(n.Id).Any() ? s.Style.ContainerFill : null), pen);
                // The component icon: a box with two tabs, top right.
                var icon = new DRect(r.Right - 24, r.Y + 6, 16, 18);
                s.Rect(icon, s.Style.Background, pen);
                s.Rect(new DRect(icon.X - 4, icon.Y + 3, 8, 4), s.Style.Background, pen);
                s.Rect(new DRect(icon.X - 4, icon.Y + 10, 8, 4), s.Style.Background, pen);
                var children = d.Children(n.Id).Any();
                s.Text(n.Text, children ? new DRect(r.X + 8, r.Y + 6, r.Width - 40, 20) : new DRect(r.X + 6, r.Y + 6, r.Width - 36, r.Height - 12),
                    TextOf(n, new DText(12.5, Bold: true, Align: children ? TextAlign.Left : TextAlign.Center)), wrap: !children);
                break;
            }
            case StructureModel.ProvidedInterface:
            {
                s.Ellipse(r, FillOf(s, n, s.Style.Background), pen);
                var style = TextOf(n, new DText(11.5));
                var (w, h) = s.Measure(n.Text, style);
                s.Text(n.Text, new DRect(r.Center.X - w / 2, r.Bottom + 2, w, h), style, wrap: false);
                break;
            }
            case StructureModel.Port:
                s.Rect(r, FillOf(s, n, s.Style.Background), pen);
                break;
            case StructureModel.Artifact:
                Document(s, n, "artifact");
                break;
            case StructureModel.Database:
                Cylinder(s, n);
                break;
            case StructureModel.Node:
                Box3D(s, n);
                break;
            case StructureModel.Device:
                Box3D(s, n, "device");
                break;
            case StructureModel.Environment:
                Box3D(s, n, "executionEnvironment");
                break;
            case StructureModel.Package:
                Folder(s, n, s.Style.ContainerFill);
                break;
            case StructureModel.Note:
                Note(s, n);
                break;
            default:
                base.DrawNode(s, d, n);
                break;
        }
    }
}
