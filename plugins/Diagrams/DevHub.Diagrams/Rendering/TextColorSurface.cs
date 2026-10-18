using Avalonia.Media;
using DevHub.Diagrams.Core;

namespace DevHub.Diagrams.Rendering;

/// <summary>
/// Draws one shape or line with its own text colour: every text a kind draws in the diagram's text or muted colour
/// (the name, a class's attributes, a stereotype, a caption, a table's columns…) takes the element's colour instead;
/// text a kind colours on purpose stays as it is. So "Text colour" reaches all of an element's text in every kind.
/// </summary>
public sealed class TextColorSurface(IDiagramSurface inner, Color color) : IDiagramSurface
{
    /// <summary>The surface to draw an element on: tinted when it has a text colour, else the surface itself.</summary>
    public static IDiagramSurface For(IDiagramSurface s, IReadOnlyDictionary<string, string> props) =>
        props.TryGetValue("textColor", out var text) && DiagramKindBase.TryColor(text) is { } c ? new TextColorSurface(s, c) : s;

    public DiagramStyle Style => inner.Style;
    public string? Folder => inner.Folder;

    private DText Tint(DText style) =>
        style.Color is null || style.Color == inner.Style.Text || style.Color == inner.Style.MutedText ? style with { Color = color } : style;

    public void Text(string text, DRect box, DText style, bool wrap = true) => inner.Text(text, box, Tint(style), wrap);
    public (double Width, double Height) Measure(string text, DText style, double maxWidth = double.PositiveInfinity) => inner.Measure(text, style, maxWidth);
    public void Rect(DRect r, Color? fill, DPen? pen, double radius = 0) => inner.Rect(r, fill, pen, radius);
    public void Ellipse(DRect r, Color? fill, DPen? pen) => inner.Ellipse(r, fill, pen);
    public void Polygon(IReadOnlyList<DPoint> points, Color? fill, DPen? pen) => inner.Polygon(points, fill, pen);
    public void Polyline(IReadOnlyList<DPoint> points, DPen pen) => inner.Polyline(points, pen);
    public void Line(DPoint a, DPoint b, DPen pen) => inner.Line(a, b, pen);
    public void Curve(IReadOnlyList<DPoint> points, DPen pen) => inner.Curve(points, pen);
    public void Image(string path, DRect box, double opacity = 1, DRect? crop = null) => inner.Image(path, box, opacity, crop);
    public IDisposable Rotate(DPoint center, double degrees) => inner.Rotate(center, degrees);
    public IDisposable Opacity(double opacity) => inner.Opacity(opacity);
}
