using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DevHub.Diagrams.Core;
using DevHub.Integration;

namespace DevHub.Diagrams.Rendering;

/// <summary>
/// Draws a whole diagram in order: containers (outer first), lines, shapes, then badges (linked issues). The canvas,
/// PNG and SVG exports all go through <see cref="Draw"/>.
/// </summary>
public static class DiagramRenderer
{
    private const double ExportMargin = 24;

    /// <param name="issues">The linked issue of a shape (for its status badge), or null.</param>
    /// <param name="hidden">Elements not to draw (the canvas draws the ones being dragged itself).</param>
    public static void Draw(IDiagramSurface s, Diagram diagram, IDiagramKind kind, Func<string, IssueLinkInfo?>? issues = null,
        IReadOnlySet<string>? hidden = null)
    {
        int Depth(DiagramNode n)
        {
            var depth = 0;
            for (var p = diagram.Node(n.ParentId); p is not null && depth < 50; p = diagram.Node(p.ParentId))
                depth++;
            return depth;
        }

        var containers = diagram.Nodes.Where(n => kind.Model.IsContainer(n)).OrderBy(Depth).ToList();
        foreach (var n in containers)
            if (hidden?.Contains(n.Id) != true)
                DrawNode(s, diagram, kind, n);

        Func<DiagramNode, NodeShape> shapeOf = kind.Model.ShapeOf;
        foreach (var e in diagram.Edges)
        {
            if (hidden?.Contains(e.Id) == true)
                continue;
            kind.DrawEdge(TextColorSurface.For(s, e.Props), diagram, e, kind.Model.PathOf(diagram, e));
        }

        foreach (var n in diagram.Nodes)
            if (!kind.Model.IsContainer(n) && hidden?.Contains(n.Id) != true)
                DrawNode(s, diagram, kind, n);

        if (issues is not null)
        {
            foreach (var n in diagram.Nodes)
            {
                var key = n.Prop("issue");
                if (key.Length > 0 && hidden?.Contains(n.Id) != true)
                    IssueBadge(s, n, key, issues(key));
            }
        }
    }

    public static void DrawNode(IDiagramSurface s, Diagram diagram, IDiagramKind kind, DiagramNode n)
    {
        using (s.Rotate(n.Center, n.Rotation))
            kind.DrawNode(TextColorSurface.For(s, n.Props), diagram, n);
    }

    /// <summary>"SHOP-12" in a pill at the shape's top right, coloured by the issue's status.</summary>
    public static void IssueBadge(IDiagramSurface s, DiagramNode n, string key, IssueLinkInfo? info)
    {
        var colour = info?.StatusCategory switch
        {
            "done" => Color.Parse("#4FAF63"),
            "inprogress" => Color.Parse("#3574F0"),
            null => Color.Parse("#9AA0A6"),
            _ => Color.Parse("#7C8089"),
        };
        var text = info?.StatusCategory == "done" ? key + " ✓" : key;
        var style = new DText(10, Bold: true, Color: Colors.White);
        var (w, h) = s.Measure(text, style);
        var box = new DRect(n.X + n.Width - w - 6, n.Y - h / 2 - 2, w + 10, h + 4);
        s.Rect(box, colour, null, box.Height / 2);
        s.Text(text, box, style, wrap: false);
    }

    /// <summary>The diagram (or the given elements) as a PNG, light, at <paramref name="scale"/> (2 = sharp in print).</summary>
    public static byte[] Png(Diagram diagram, IDiagramKind kind, DiagramStyle style, IReadOnlyCollection<string>? only = null, double scale = 2, string? folder = null)
    {
        var part = Part(diagram, only);
        var area = Area(part);
        var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(area.Width * scale)), Math.Max(1, (int)Math.Ceiling(area.Height * scale)));
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(new SolidColorBrush(style.Background), new Rect(0, 0, area.Width, area.Height));
            using (context.PushTransform(Matrix.CreateTranslation(-area.X, -area.Y)))
                Draw(new AvaloniaSurface(context, style, folder), part, kind);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    /// <summary>The diagram (or the given elements) as an SVG document.</summary>
    public static string Svg(Diagram diagram, IDiagramKind kind, DiagramStyle style, IReadOnlyCollection<string>? only = null, string? folder = null)
    {
        var part = Part(diagram, only);
        var svg = new SvgSurface(style, folder);
        Draw(svg, part, kind);
        return svg.Document(Area(part));
    }

    private static DRect Area(Diagram diagram)
    {
        var bounds = diagram.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            bounds = new DRect(0, 0, 200, 120);
        // Room for labels and badges that stick out of the shapes.
        return bounds.Inflate(ExportMargin);
    }

    /// <summary>A copy with only the given shapes (and what's inside them) and the lines between them.</summary>
    private static Diagram Part(Diagram diagram, IReadOnlyCollection<string>? only)
    {
        if (only is null || only.Count == 0)
            return diagram;
        var copy = diagram.Clone();
        var keep = only.SelectMany(id => copy.WithDescendants(id)).Select(n => n.Id).ToHashSet();
        copy.Nodes.RemoveAll(n => !keep.Contains(n.Id));
        copy.Edges.RemoveAll(e => !(keep.Contains(e.From) && keep.Contains(e.To)) && !only.Contains(e.Id));
        copy.Edges.RemoveAll(e => !keep.Contains(e.From) || !keep.Contains(e.To));
        return copy;
    }
}
