using Avalonia.Media;
using DevHub.Diagrams.Core;

namespace DevHub.Diagrams.Rendering;

/// <summary>Measures text for kinds that size shapes to their text (<see cref="IDiagramKind.Fit"/>), drawing nothing.</summary>
public sealed class MeasureSurface : IDiagramSurface
{
    public static readonly MeasureSurface Instance = new();

    public DiagramStyle Style => DiagramStyle.Light;
    public void Rect(DRect r, Color? fill, DPen? pen, double radius = 0) { }
    public void Ellipse(DRect r, Color? fill, DPen? pen) { }
    public void Polygon(IReadOnlyList<DPoint> points, Color? fill, DPen? pen) { }
    public void Polyline(IReadOnlyList<DPoint> points, DPen pen) { }
    public void Line(DPoint a, DPoint b, DPen pen) { }
    public void Curve(IReadOnlyList<DPoint> points, DPen pen) { }
    public void Text(string text, DRect box, DText style, bool wrap = true) { }

    public (double Width, double Height) Measure(string text, DText style, double maxWidth = double.PositiveInfinity) =>
        AvaloniaSurface.MeasureText(text, style, maxWidth);

    public void Image(string path, DRect box, double opacity = 1, DRect? crop = null) { }
    public IDisposable Rotate(DPoint center, double degrees) => Done.Instance;
    public IDisposable Opacity(double opacity) => Done.Instance;

    private sealed class Done : IDisposable
    {
        public static readonly Done Instance = new();
        public void Dispose() { }
    }

    /// <summary>Sizes every shape of a diagram to its text (a sample or a generated diagram, before it's first shown).</summary>
    public static void FitAll(Diagram diagram, IDiagramKind kind)
    {
        foreach (var node in diagram.Nodes)
            if (kind.Fit(node, Instance) is { } fit)
                (node.X, node.Y, node.Width, node.Height) = (fit.X, fit.Y, fit.Width, fit.Height);
    }
}
