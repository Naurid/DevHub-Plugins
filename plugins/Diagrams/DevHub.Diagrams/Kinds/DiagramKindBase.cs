using Avalonia.Media;
using DevHub.Diagrams.Core;
using DevHub.Diagrams.Rendering;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams;

/// <summary>Arrowheads and line ends.</summary>
public enum LineEnd { None, Open, Hollow, Filled, HollowDiamond, FilledDiamond, Cross, Circle }

/// <summary>How a line looks: dash and both ends.</summary>
public sealed record EdgeLook(StrokeDash Dash = StrokeDash.Solid, LineEnd Start = LineEnd.None, LineEnd End = LineEnd.None);

/// <summary>
/// The defaults kinds start from: shapes drawn by their outline with their text centred, lines with their look's
/// arrowheads and their text as a label, a "Name" field. Kinds override what they draw differently.
/// </summary>
public abstract class DiagramKindBase : IDiagramKind
{
    public abstract IDiagramKindModel Model { get; }
    public abstract string IconKind { get; }

    /// <summary>An icon the kind's plugin ships in <c>Assets/Icons/</c> (<c>name.svg</c> and <c>name_dark.svg</c>).</summary>
    protected string KindIcon(string name) => $"avares://{GetType().Assembly.GetName().Name}/Assets/Icons/{name}";
    public virtual int Order => 100;
    public virtual bool HasPen => false;
    public virtual IReadOnlyList<KindCommand> Commands => [];
    public virtual Task<bool> PasteAsync(IDiagramEditor editor, DPoint at) => Task.FromResult(false);
    public virtual Task<bool> DropFilesAsync(IDiagramEditor editor, IReadOnlyList<string> paths, DPoint at) => Task.FromResult(false);
    public virtual IDiagramOperation? Stroke(Diagram diagram, IReadOnlyList<DPoint> points, string color, double width) => null;

    public virtual IReadOnlyList<PropertyField> NodeFields(DiagramNode node) => [new(L.T("Name"), "@text"), .. StyleFields];

    public virtual IReadOnlyList<PropertyField> EdgeFields(DiagramEdge edge) =>
    [
        new(L.T("Label"), "@text"),
        new(L.T("Route"), "@routing", FieldKind.Choice)
        {
            Choices = [new("straight", L.T("Straight")), new("orthogonal", L.T("Right angles"))],
        },
        .. LineStyleFields,
    ];

    // ---- Formatting any shape or line can have (like Visual Paradigm's Format panel) ----

    /// <summary>Fill, line and text colours, text size and weight: the formatting fields every shape gets.</summary>
    public static IReadOnlyList<PropertyField> StyleFields { get; } =
    [
        new(L.T("Fill colour"), "fill", FieldKind.Color, L.T("Empty: the diagram's colour")),
        new(L.T("Line colour"), "line", FieldKind.Color, L.T("Empty: the diagram's colour")),
        new(L.T("Text colour"), "textColor", FieldKind.Color, L.T("Empty: the diagram's colour")),
        new(L.T("Text size"), "fontSize", FieldKind.Number) { Default = "13", Minimum = 8, Maximum = 36, Step = 1 },
        new(L.T("Bold"), "bold", FieldKind.Toggle),
    ];

    /// <summary>Colour, width, dash and arrowheads of a line (empty choices keep what the notation draws).</summary>
    public static IReadOnlyList<PropertyField> LineStyleFields { get; } =
    [
        new(L.T("Line colour"), "line", FieldKind.Color, L.T("Empty: the diagram's colour")),
        new(L.T("Line width"), "width", FieldKind.Number) { Default = "1.2", Minimum = 0.5, Maximum = 8, Step = 0.1 },
        new(L.T("Text colour"), "textColor", FieldKind.Color, L.T("Empty: the diagram's colour")),
        LabelBackgroundField,
        new(L.T("Dash"), "dash", FieldKind.Choice) { Choices = DashChoices },
        new(L.T("Arrow at the start"), "startArrow", FieldKind.Choice) { Choices = ArrowChoices },
        new(L.T("Arrow at the end"), "endArrow", FieldKind.Choice) { Choices = ArrowChoices },
    ];

    /// <summary>The colour behind a line's label ("labelFill"): empty = the diagram's background, transparent = none.</summary>
    /// <remarks>A computed property, not a stored one: <see cref="LineStyleFields"/> (initialised first) lists it.</remarks>
    public static PropertyField LabelBackgroundField =>
        new(L.T("Label background"), LabelFill, FieldKind.Color, L.T("Empty: the diagram's background. Transparent: none."));

    public const string LabelFill = "labelFill";

    public static IReadOnlyList<FieldChoice> DashChoices =>
    [
        new("", L.T("As the notation draws it")), new("solid", L.T("Solid")), new("dashed", L.T("Dashed")), new("dotted", L.T("Dotted")),
    ];

    public static IReadOnlyList<FieldChoice> ArrowChoices =>
    [
        new("", L.T("As the notation draws it")), new("none", L.T("None")), new("open", L.T("Open arrow")),
        new("filled", L.T("Filled arrow")), new("hollow", L.T("Hollow triangle")), new("diamond", L.T("Hollow diamond")),
        new("filledDiamond", L.T("Filled diamond")), new("circle", L.T("Circle")), new("cross", L.T("Cross")),
    ];

    /// <summary>The line's own arrowhead at its start, or <paramref name="notation"/> when it has none set.</summary>
    public static LineEnd StartOf(DiagramEdge e, LineEnd notation) => ArrowOf(e.Prop("startArrow")) ?? notation;

    /// <summary>The line's own arrowhead at its end, or <paramref name="notation"/> when it has none set.</summary>
    public static LineEnd EndOf(DiagramEdge e, LineEnd notation) => ArrowOf(e.Prop("endArrow")) ?? notation;

    /// <summary>Whether the user chose an arrowhead for that end (kinds drawing their own ends, like crow's feet, give way).</summary>
    public static bool HasArrow(DiagramEdge e, bool atStart) => ArrowOf(e.Prop(atStart ? "startArrow" : "endArrow")) is not null;

    private static LineEnd? ArrowOf(string value) => value switch
    {
        "none" => LineEnd.None,
        "open" => LineEnd.Open,
        "filled" => LineEnd.Filled,
        "hollow" => LineEnd.Hollow,
        "diamond" => LineEnd.HollowDiamond,
        "filledDiamond" => LineEnd.FilledDiamond,
        "circle" => LineEnd.Circle,
        "cross" => LineEnd.Cross,
        _ => null,
    };

    /// <summary>The line's own dash, or <paramref name="notation"/>.</summary>
    public static StrokeDash DashOf(DiagramEdge e, StrokeDash notation) => e.Prop("dash") switch
    {
        "solid" => StrokeDash.Solid,
        "dashed" => StrokeDash.Dashed,
        "dotted" => StrokeDash.Dotted,
        _ => notation,
    };

    /// <summary>A shape's fill: its own, or <paramref name="fallback"/> (default: the diagram's).</summary>
    public static Color FillOf(IDiagramSurface s, DiagramNode n, Color? fallback = null) =>
        TryColor(n.Prop("fill")) ?? fallback ?? s.Style.Fill;

    /// <summary>A shape's outline pen: its own colour, or the diagram's line colour.</summary>
    public static DPen PenOf(IDiagramSurface s, DiagramNode n, double thickness = 1.2, StrokeDash dash = StrokeDash.Solid) =>
        new(TryColor(n.Prop("line")) ?? s.Style.Line, thickness, dash);

    /// <summary>A shape's text style: its own colour, size and weight over <paramref name="style"/>.</summary>
    public static DText TextOf(DiagramNode n, DText style) =>
        style with
        {
            Color = TryColor(n.Prop("textColor")) ?? style.Color,
            Size = double.TryParse(n.Prop("fontSize"), out var size) && size is >= 6 and <= 96 ? size : style.Size,
            Bold = style.Bold || n.Flag("bold"),
        };

    /// <summary>A line's pen: its own colour and width over the diagram's.</summary>
    public static DPen PenOf(IDiagramSurface s, DiagramEdge e, StrokeDash dash = StrokeDash.Solid) =>
        new(TryColor(e.Prop("line")) ?? s.Style.Line,
            double.TryParse(e.Prop("width"), out var w) && w is > 0 and <= 20 ? w : 1.2, DashOf(e, dash));

    /// <summary>"#RRGGBB" (or #AARRGGBB, or a colour name) as a colour, or null.</summary>
    public static Color? TryColor(string text) =>
        text.Length > 0 && Color.TryParse(text.Trim(), out var c) ? c : null;

    /// <summary>
    /// Text that reads on <paramref name="fill"/> (blended over <paramref name="behind"/> when it's see-through): near
    /// black on light colours, near white on dark ones, so a shape filled light stays readable in the dark theme.
    /// </summary>
    public static (Color Text, Color Muted) ContrastOn(Color fill, Color behind)
    {
        var a = fill.A / 255.0;
        double Mix(byte f, byte b) => (f * a + b * (1 - a)) / 255.0;
        static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        var luminance = 0.2126 * Linear(Mix(fill.R, behind.R)) + 0.7152 * Linear(Mix(fill.G, behind.G)) + 0.0722 * Linear(Mix(fill.B, behind.B));
        return luminance > 0.36
            ? (Color.Parse("#1F1F1F"), Color.Parse("#5C6068"))
            : (Color.Parse("#F0F1F3"), Color.Parse("#B4B8BF"));
    }

    public virtual IReadOnlyList<PropertyField> DiagramFields => [new(L.T("Title"), "@title")];

    public virtual DRect? Fit(DiagramNode node, IDiagramSurface measure) => null;

    public virtual bool MultilineText(DiagramNode node) => false;

    /// <summary>How a line of this type looks (default: a plain line).</summary>
    protected virtual EdgeLook Look(DiagramEdge edge) => new();

    public virtual void DrawNode(IDiagramSurface s, Diagram diagram, DiagramNode node)
    {
        var shape = Model.ShapeOf(node);
        var r = node.Bounds;
        var pen = PenOf(s, node);
        var fill = FillOf(s, node, Model.IsContainer(node) ? s.Style.ContainerFill : null);
        var text = TextOf(node, new DText());
        switch (shape)
        {
            case NodeShape.Ellipse:
                s.Ellipse(r, fill, pen);
                s.Text(node.Text, r.Inflate(-r.Width * 0.12), text);
                break;
            case NodeShape.Diamond:
                s.Polygon(Diamond(r), fill, pen);
                s.Text(node.Text, r.Inflate(-r.Width * 0.2), text with { Size = text.Size - 1 });
                break;
            case NodeShape.Figure:
                StickFigure(s, node, pen);
                break;
            case NodeShape.Bar:
                s.Rect(r, TryColor(node.Prop("fill")) ?? pen.Color, null, 1);
                break;
            case NodeShape.RoundedRectangle:
                s.Rect(r, fill, pen, Math.Min(14, r.Height / 2));
                s.Text(node.Text, r.Inflate(-6), text);
                break;
            default:
                s.Rect(r, fill, pen);
                s.Text(node.Text, r.Inflate(-6), text);
                break;
        }
    }

    public virtual void DrawEdge(IDiagramSurface s, Diagram diagram, DiagramEdge edge, IReadOnlyList<DPoint> path)
    {
        if (path.Count < 2)
            return;
        var look = Look(edge);
        var pen = PenOf(s, edge, look.Dash);
        s.Polyline(path, pen);
        DrawEnd(s, path[^1], path[^2], EndOf(edge, look.End), pen.Color);
        DrawEnd(s, path[0], path[1], StartOf(edge, look.Start), pen.Color);
        if (edge.Text.Length > 0)
            Label(s, edge.Text, LabelAnchor(diagram, edge, path), edge: edge);
    }

    /// <summary>Where the line's label is drawn (and dragged from): its point on the line plus its offset.</summary>
    public virtual DPoint LabelAnchor(Diagram diagram, DiagramEdge edge, IReadOnlyList<DPoint> path) => Routing.LabelCenter(path, edge);

    /// <summary>
    /// The full text of a shape whose text doesn't fit where it's drawn (a long package name ends in "…"), shown on hover
    /// and while it's selected; null when all of it shows.
    /// </summary>
    public virtual string? HiddenText(Diagram diagram, DiagramNode node, IDiagramSurface measure) => null;

    // ---- Helpers for kinds ----

    public static IReadOnlyList<DPoint> Diamond(DRect r) =>
        [new(r.Center.X, r.Top), new(r.Right, r.Center.Y), new(r.Center.X, r.Bottom), new(r.Left, r.Center.Y)];

    /// <summary>
    /// A line label on a small background, so it stays readable across the line. With <paramref name="edge"/>, its
    /// "Label background" is used: a colour (the text then contrasts with it), or transparent for none.
    /// </summary>
    public static void Label(IDiagramSurface s, string text, DPoint at, double size = 11, Color? color = null, bool italic = false,
        DiagramEdge? edge = null)
    {
        var style = new DText(size, Italic: italic, Color: color);
        var box = LabelBox(s, text, at, style);
        var background = s.Style.Background;
        if (edge is not null && TryColor(edge.Prop(LabelFill)) is { } own)
        {
            background = own;
            if (own.A > 0 && edge.Prop("textColor").Length == 0)
            {
                var (onFill, muted) = ContrastOn(own, s.Style.Background);
                style = style with { Color = color == s.Style.MutedText ? muted : onFill };
            }
        }
        if (background.A > 0)
            s.Rect(box, background, null, 2);
        s.Text(text, box, style, wrap: false);
    }

    /// <summary>The box a label takes around <paramref name="at"/> (what the canvas picks it up by).</summary>
    public static DRect LabelBox(IDiagramSurface s, string text, DPoint at, DText style)
    {
        var (w, h) = s.Measure(text, style);
        return new DRect(at.X - w / 2 - 3, at.Y - h / 2 - 1, w + 6, h + 2);
    }

    /// <summary>A text near the end of a line (multiplicities, roles, cardinalities), set off to one side.</summary>
    public static void EndLabel(IDiagramSurface s, string text, IReadOnlyList<DPoint> path, bool atStart, double side = 1)
    {
        if (text.Length == 0 || path.Count < 2)
            return;
        var (end, next) = atStart ? (path[0], path[1]) : (path[^1], path[^2]);
        var d = next - end;
        var len = d.Length;
        if (len == 0)
            return;
        var u = d * (1 / len);
        var normal = new DPoint(-u.Y, u.X) * side;
        var at = end + u * 14 + normal * 10;
        var style = new DText(11);
        var (w, h) = s.Measure(text, style);
        s.Text(text, new DRect(at.X - w / 2, at.Y - h / 2, w, h), style, wrap: false);
    }

    /// <summary>An arrowhead or line end at <paramref name="tip"/>, pointing away from <paramref name="from"/>.</summary>
    /// <summary>Both ends of a line: the notation's arrowheads unless the line has its own (Arrow at the start / end).</summary>
    public static void DrawEnds(IDiagramSurface s, DiagramEdge e, IReadOnlyList<DPoint> path, LineEnd start, LineEnd end, Color color)
    {
        if (path.Count < 2)
            return;
        DrawEnd(s, path[^1], path[^2], EndOf(e, end), color);
        DrawEnd(s, path[0], path[1], StartOf(e, start), color);
    }

    public static void DrawEnd(IDiagramSurface s, DPoint tip, DPoint from, LineEnd end, Color? color = null)
    {
        if (end == LineEnd.None)
            return;
        var d = tip - from;
        var len = d.Length;
        if (len == 0)
            return;
        var u = d * (1 / len);
        var n = new DPoint(-u.Y, u.X);
        var line = color ?? s.Style.Line;
        var pen = new DPen(line, 1.2);
        switch (end)
        {
            case LineEnd.Open:
                s.Polyline([tip - u * 11 + n * 5.5, tip, tip - u * 11 - n * 5.5], pen);
                break;
            case LineEnd.Hollow:
                s.Polygon([tip, tip - u * 14 + n * 8, tip - u * 14 - n * 8], s.Style.Background, pen);
                break;
            case LineEnd.Filled:
                s.Polygon([tip, tip - u * 11 + n * 5, tip - u * 11 - n * 5], line, pen);
                break;
            case LineEnd.HollowDiamond:
            case LineEnd.FilledDiamond:
                s.Polygon([tip, tip - u * 9 + n * 5.5, tip - u * 18, tip - u * 9 - n * 5.5],
                    end == LineEnd.FilledDiamond ? line : s.Style.Background, pen);
                break;
            case LineEnd.Cross:
                var c = tip - u * 6;
                s.Line(c + (u + n) * 4, c - (u + n) * 4, pen);
                s.Line(c + (u - n) * 4, c - (u - n) * 4, pen);
                break;
            case LineEnd.Circle:
                s.Ellipse(new DRect(tip.X - u.X * 5 - 4, tip.Y - u.Y * 5 - 4, 8, 8), s.Style.Background, pen);
                break;
        }
    }

    /// <summary>A stick figure (actor) filling the top of the box, its name below.</summary>
    public static void StickFigure(IDiagramSurface s, DiagramNode node, DPen pen, string? stereotype = null)
    {
        var r = node.Bounds;
        var textStyle = new DText(12, Bold: true);
        var (_, textHeight) = s.Measure(node.Text, textStyle, Math.Max(r.Width, 120));
        var figure = r with { Height = Math.Max(20, r.Height - textHeight - 2) };
        var cx = figure.Center.X;
        var head = Math.Min(figure.Width, figure.Height) * 0.18;
        s.Ellipse(new DRect(cx - head, figure.Top, head * 2, head * 2), s.Style.Fill, pen);
        var neck = figure.Top + head * 2;
        var hip = figure.Top + figure.Height * 0.68;
        s.Line(new DPoint(cx, neck), new DPoint(cx, hip), pen);
        var arms = neck + (hip - neck) * 0.3;
        s.Line(new DPoint(cx - figure.Width * 0.38, arms), new DPoint(cx + figure.Width * 0.38, arms), pen);
        s.Line(new DPoint(cx, hip), new DPoint(cx - figure.Width * 0.3, figure.Bottom), pen);
        s.Line(new DPoint(cx, hip), new DPoint(cx + figure.Width * 0.3, figure.Bottom), pen);
        var label = new DRect(r.Center.X - Math.Max(r.Width, 120) / 2, figure.Bottom + 2, Math.Max(r.Width, 120), textHeight);
        s.Text(node.Text, label, textStyle with { Vertical = TextVertical.Top });
    }

    /// <summary>A note: a rectangle with its top-right corner folded, its text inside.</summary>
    public static void Note(IDiagramSurface s, DiagramNode n)
    {
        var r = n.Bounds;
        const double fold = 12;
        var pen = PenOf(s, n);
        s.Polygon([new(r.Left, r.Top), new(r.Right - fold, r.Top), new(r.Right, r.Top + fold), new(r.Right, r.Bottom), new(r.Left, r.Bottom)],
            FillOf(s, n, s.Style.NoteFill), pen);
        s.Polyline([new(r.Right - fold, r.Top), new(r.Right - fold, r.Top + fold), new(r.Right, r.Top + fold)], pen);
        s.Text(n.Text, r.Inflate(-8), TextOf(n, new DText(12, Align: TextAlign.Left, Vertical: TextVertical.Top)));
    }

    /// <summary>
    /// A folder (UML package): a tab on the top left. A package with shapes inside has its name in the tab, which grows
    /// with the name up to the package's width (a longer name ends in "…": <see cref="FolderHiddenText"/>); an empty
    /// package has a small tab and its name in the body, wrapped, as UML draws it.
    /// </summary>
    public static void Folder(IDiagramSurface s, DiagramNode n, Color? fill = null, bool hasContents = true)
    {
        var r = n.Bounds;
        var pen = PenOf(s, n);
        var name = FolderName(n);
        var (w, h) = s.Measure(n.Text, name);
        var tabHeight = h + 8;
        var tab = new DRect(r.X, r.Y, hasContents ? Math.Min(r.Width, Math.Max(60, w + 16)) : Math.Min(r.Width, 48), tabHeight);
        s.Rect(tab, FillOf(s, n, fill), pen);
        var body = new DRect(r.X, tab.Bottom, r.Width, Math.Max(0, r.Height - tab.Height));
        s.Rect(body, FillOf(s, n, fill), pen);
        if (hasContents)
            s.Text(Ellipsize(s, n.Text, name, tab.Width - 16), tab.Inflate(-4) with { X = tab.X + 8, Width = tab.Width - 12 }, name, wrap: false);
        else
        {
            // Wrapped in the body; when even that doesn't fit, one line ending in "…" (nothing bleeds out).
            var inner = body.Inflate(-8);
            var centred = name with { Align = TextAlign.Center };
            if (s.Measure(n.Text, centred, Math.Max(1, inner.Width)).Height <= inner.Height)
                s.Text(n.Text, inner, centred);
            else
                s.Text(Ellipsize(s, n.Text, centred, inner.Width), inner, centred, wrap: false);
        }
    }

    /// <summary>The package's name when its tab cuts it short, else null.</summary>
    public static string? FolderHiddenText(IDiagramSurface measure, DiagramNode n, bool hasContents)
    {
        var name = FolderName(n);
        if (hasContents)
            return measure.Measure(n.Text, name).Width + 16 > n.Width ? n.Text : null;
        var tab = measure.Measure("Ag", name).Height + 8;
        return measure.Measure(n.Text, name with { Align = TextAlign.Center }, Math.Max(1, n.Width - 16)).Height > n.Height - tab - 16 ? n.Text : null;
    }

    private static DText FolderName(DiagramNode n) => TextOf(n, new DText(12, Bold: true, Align: TextAlign.Left));

    /// <summary><paramref name="text"/> cut to <paramref name="width"/> with "…" at the end (as it is when it fits).</summary>
    public static string Ellipsize(IDiagramSurface s, string text, DText style, double width)
    {
        if (s.Measure(text, style).Width <= width)
            return text;
        var (low, high) = (0, text.Length);
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (s.Measure(text[..mid].TrimEnd() + "…", style).Width <= width)
                low = mid;
            else
                high = mid - 1;
        }
        return low == 0 ? "…" : text[..low].TrimEnd() + "…";
    }

    /// <summary>A cylinder (a database, a data store).</summary>
    public static void Cylinder(IDiagramSurface s, DiagramNode n)
    {
        var r = n.Bounds;
        var pen = PenOf(s, n);
        var fill = FillOf(s, n);
        var lip = Math.Min(14, r.Height * 0.2);
        s.Rect(new DRect(r.X, r.Y + lip / 2, r.Width, r.Height - lip), fill, null);
        s.Ellipse(new DRect(r.X, r.Bottom - lip, r.Width, lip), fill, pen);
        s.Rect(new DRect(r.X + 0.6, r.Y + lip / 2, r.Width - 1.2, r.Height - lip), fill, null);
        s.Line(new DPoint(r.X, r.Y + lip / 2), new DPoint(r.X, r.Bottom - lip / 2), pen);
        s.Line(new DPoint(r.Right, r.Y + lip / 2), new DPoint(r.Right, r.Bottom - lip / 2), pen);
        s.Ellipse(new DRect(r.X, r.Y, r.Width, lip), fill, pen);
        s.Text(n.Text, new DRect(r.X + 4, r.Y + lip, r.Width - 8, r.Height - lip * 1.5), TextOf(n, new DText(12)));
    }

    /// <summary>A 3D box (a UML node): the front face with the name, the top and right faces.</summary>
    public static void Box3D(IDiagramSurface s, DiagramNode n, string? stereotype = null)
    {
        var r = n.Bounds;
        const double depth = 12;
        var pen = PenOf(s, n);
        var fill = FillOf(s, n, s.Style.ContainerFill);
        var front = new DRect(r.X, r.Y + depth, r.Width - depth, r.Height - depth);
        s.Polygon([new(front.Left, front.Top), new(front.Left + depth, r.Y), new(r.Right, r.Y), new(front.Right, front.Top)], fill, pen);
        s.Polygon([new(front.Right, front.Top), new(r.Right, r.Y), new(r.Right, front.Bottom - depth), new(front.Right, front.Bottom)], fill, pen);
        s.Rect(front, fill, pen);
        var top = front.Y + 6;
        if (stereotype is { Length: > 0 })
        {
            s.Text(Guillemets(stereotype), new DRect(front.X + 4, top, front.Width - 8, 16), new DText(11, Color: s.Style.MutedText), wrap: false);
            top += 16;
        }
        s.Text(n.Text, new DRect(front.X + 4, top, front.Width - 8, 18), TextOf(n, new DText(12, Bold: true)), wrap: false);
    }

    /// <summary>A document (an artifact, a data object): a page with its top-right corner folded.</summary>
    public static void Document(IDiagramSurface s, DiagramNode n, string? stereotype = null)
    {
        var r = n.Bounds;
        const double fold = 10;
        var pen = PenOf(s, n);
        s.Polygon([new(r.Left, r.Top), new(r.Right - fold, r.Top), new(r.Right, r.Top + fold), new(r.Right, r.Bottom), new(r.Left, r.Bottom)], FillOf(s, n), pen);
        s.Polyline([new(r.Right - fold, r.Top), new(r.Right - fold, r.Top + fold), new(r.Right, r.Top + fold)], pen);
        var body = r.Inflate(-6);
        if (stereotype is { Length: > 0 })
        {
            s.Text(Guillemets(stereotype), body with { Height = 16 }, new DText(11, Color: s.Style.MutedText), wrap: false);
            body = body with { Y = body.Y + 14, Height = body.Height - 14 };
        }
        s.Text(n.Text, body, TextOf(n, new DText(12)));
    }

    /// <summary>«stereotype» in guillemets (French and UML both use them).</summary>
    public static string Guillemets(string stereotype) =>
        stereotype.Length == 0 ? "" : stereotype.StartsWith('«') ? stereotype : $"«{stereotype.Trim('<', '>', ' ')}»";
}
