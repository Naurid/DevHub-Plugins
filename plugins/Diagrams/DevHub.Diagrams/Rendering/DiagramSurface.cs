using Avalonia.Media;
using DevHub.Diagrams.Core;

namespace DevHub.Diagrams.Rendering;

public enum StrokeDash { Solid, Dashed, Dotted }

/// <summary>How a line is drawn.</summary>
public sealed record DPen(Color Color, double Thickness = 1.2, StrokeDash Dash = StrokeDash.Solid);

public enum TextAlign { Left, Center, Right }

public enum TextVertical { Top, Center, Bottom }

/// <summary>How a text is drawn (null colour = the style's text colour).</summary>
public sealed record DText(double Size = 13, bool Bold = false, bool Italic = false, bool Underline = false,
    Color? Color = null, TextAlign Align = TextAlign.Center, TextVertical Vertical = TextVertical.Center, bool Mono = false);

/// <summary>
/// The colours a diagram is drawn with. <see cref="Light"/> for exports (they go into documents and print), the app's
/// theme for the canvas; <see cref="CourseColours"/> fills shapes like the course's tool (blue actions, yellow lanes).
/// </summary>
public sealed record DiagramStyle(Color Background, Color Line, Color Fill, Color Text, Color MutedText, Color Accent,
    Color Selection, Color Grid, Color ContainerFill, Color NoteFill, bool CourseColours = false)
{
    public static DiagramStyle Light { get; } = new(
        Colors.White, Color.Parse("#1F1F1F"), Colors.White, Color.Parse("#1F1F1F"), Color.Parse("#6B6F76"),
        Color.Parse("#3574F0"), Color.Parse("#3574F0"), Color.Parse("#ECEDF0"), Color.Parse("#FAFAFB"), Color.Parse("#FFF8C5"));

    public static DiagramStyle Dark { get; } = new(
        Color.Parse("#1E1F22"), Color.Parse("#CED0D6"), Color.Parse("#2B2D30"), Color.Parse("#DFE1E5"), Color.Parse("#868A91"),
        Color.Parse("#3574F0"), Color.Parse("#548AF7"), Color.Parse("#2B2D30"), Color.Parse("#232427"), Color.Parse("#3D3A26"));

    /// <summary>The course's look: light blue actions and use cases, pale yellow lanes and boundaries.</summary>
    public DiagramStyle WithCourseColours() => this with
    {
        CourseColours = true,
        Fill = Color.Parse("#7ACFF5"),
        ContainerFill = Color.Parse("#FFF0A8"),
    };
}

/// <summary>
/// Where a diagram is drawn: the canvas, a PNG, an SVG. Kinds draw with these few primitives, so every export looks
/// like the canvas. Coordinates are diagram coordinates.
/// </summary>
public interface IDiagramSurface
{
    DiagramStyle Style { get; }

    /// <summary>The diagram file's folder: pictures stored beside it (mood boards' <c>.assets</c>) resolve against it.</summary>
    string? Folder => null;

    void Rect(DRect r, Color? fill, DPen? pen, double radius = 0);

    void Ellipse(DRect r, Color? fill, DPen? pen);

    void Polygon(IReadOnlyList<DPoint> points, Color? fill, DPen? pen);

    void Polyline(IReadOnlyList<DPoint> points, DPen pen);

    void Line(DPoint a, DPoint b, DPen pen);

    /// <summary>A smooth line through points (freehand strokes).</summary>
    void Curve(IReadOnlyList<DPoint> points, DPen pen);

    /// <summary>Draws text in a box (wrapped to its width unless <paramref name="wrap"/> is false).</summary>
    void Text(string text, DRect box, DText style, bool wrap = true);

    /// <summary>The size a text takes (wrapped at <paramref name="maxWidth"/>).</summary>
    (double Width, double Height) Measure(string text, DText style, double maxWidth = double.PositiveInfinity);

    /// <summary>A picture file, stretched into <paramref name="box"/>, cropped to <paramref name="crop"/> (fractions 0–1).</summary>
    void Image(string path, DRect box, double opacity = 1, DRect? crop = null);

    /// <summary>Turns what's drawn until the result is disposed around <paramref name="center"/>.</summary>
    IDisposable Rotate(DPoint center, double degrees);

    IDisposable Opacity(double opacity);
}
