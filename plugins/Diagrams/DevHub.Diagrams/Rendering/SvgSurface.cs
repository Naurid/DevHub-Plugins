using System.Globalization;
using System.Net;
using System.Text;
using Avalonia.Media;
using DevHub.Diagrams.Core;

namespace DevHub.Diagrams.Rendering;

/// <summary>Writes SVG (for documents that scale: Word, LaTeX, the web). Text is measured like on the canvas.</summary>
public sealed class SvgSurface(DiagramStyle style, string? folder = null) : IDiagramSurface
{
    private readonly StringBuilder _body = new();
    private int _groups;

    public DiagramStyle Style { get; } = style;

    public string? Folder => folder;

    /// <summary>The whole SVG document for the area <paramref name="view"/>.</summary>
    public string Document(DRect view)
    {
        var sb = new StringBuilder();
        sb.Append(Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{view.Width:0.##}\" height=\"{view.Height:0.##}\" viewBox=\"{view.X:0.##} {view.Y:0.##} {view.Width:0.##} {view.Height:0.##}\" font-family=\"Inter, Segoe UI, Arial, sans-serif\">\n"));
        sb.Append(Invariant($"<rect x=\"{view.X:0.##}\" y=\"{view.Y:0.##}\" width=\"{view.Width:0.##}\" height=\"{view.Height:0.##}\" fill=\"{Hex(Style.Background)}\"/>\n"));
        sb.Append(_body);
        for (var i = 0; i < _groups; i++)
            sb.Append("</g>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    public void Rect(DRect r, Color? fill, DPen? pen, double radius = 0) =>
        _body.Append(Invariant($"<rect x=\"{r.X:0.##}\" y=\"{r.Y:0.##}\" width=\"{r.Width:0.##}\" height=\"{r.Height:0.##}\" rx=\"{radius:0.##}\" {Paint(fill, pen)}/>\n"));

    public void Ellipse(DRect r, Color? fill, DPen? pen) =>
        _body.Append(Invariant($"<ellipse cx=\"{r.Center.X:0.##}\" cy=\"{r.Center.Y:0.##}\" rx=\"{r.Width / 2:0.##}\" ry=\"{r.Height / 2:0.##}\" {Paint(fill, pen)}/>\n"));

    public void Polygon(IReadOnlyList<DPoint> points, Color? fill, DPen? pen) =>
        _body.Append($"<polygon points=\"{Points(points)}\" {Paint(fill, pen)}/>\n");

    public void Polyline(IReadOnlyList<DPoint> points, DPen pen) =>
        _body.Append($"<polyline points=\"{Points(points)}\" {Paint(null, pen)}/>\n");

    public void Line(DPoint a, DPoint b, DPen pen) => Polyline([a, b], pen);

    public void Curve(IReadOnlyList<DPoint> points, DPen pen)
    {
        if (points.Count < 3)
        {
            Polyline(points, pen);
            return;
        }
        var d = new StringBuilder(Invariant($"M{points[0].X:0.##},{points[0].Y:0.##}"));
        for (var i = 1; i < points.Count - 1; i++)
        {
            var mid = points[i] + (points[i + 1] - points[i]) * 0.5;
            d.Append(Invariant($" Q{points[i].X:0.##},{points[i].Y:0.##} {mid.X:0.##},{mid.Y:0.##}"));
        }
        d.Append(Invariant($" L{points[^1].X:0.##},{points[^1].Y:0.##}"));
        _body.Append($"<path d=\"{d}\" {Paint(null, pen)}/>\n");
    }

    public void Text(string text, DRect box, DText style, bool wrap = true)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var lines = wrap ? Wrap(text, style, box.Width) : text.Split('\n').ToList();
        var lineHeight = AvaloniaSurface.MeasureText("Ag", style, double.PositiveInfinity).Height;
        var total = lineHeight * lines.Count;
        var top = style.Vertical switch
        {
            TextVertical.Top => box.Y,
            TextVertical.Bottom => box.Bottom - total,
            _ => box.Center.Y - total / 2,
        };
        var (anchor, x) = style.Align switch
        {
            TextAlign.Left => ("start", box.X),
            TextAlign.Right => ("end", box.Right),
            _ => ("middle", box.Center.X),
        };
        var attributes = new StringBuilder(Invariant($"font-size=\"{style.Size:0.##}\" fill=\"{Hex(style.Color ?? Style.Text)}\" text-anchor=\"{anchor}\""));
        if (style.Bold)
            attributes.Append(" font-weight=\"600\"");
        if (style.Italic)
            attributes.Append(" font-style=\"italic\"");
        if (style.Underline)
            attributes.Append(" text-decoration=\"underline\"");
        if (style.Mono)
            attributes.Append(" font-family=\"JetBrains Mono, Consolas, monospace\"");
        for (var i = 0; i < lines.Count; i++)
        {
            var baseline = top + lineHeight * i + lineHeight * 0.78;
            _body.Append(Invariant($"<text x=\"{x:0.##}\" y=\"{baseline:0.##}\" {attributes}>{WebUtility.HtmlEncode(lines[i])}</text>\n"));
        }
    }

    public (double Width, double Height) Measure(string text, DText style, double maxWidth = double.PositiveInfinity) =>
        AvaloniaSurface.MeasureText(text, style, maxWidth);

    public void Image(string path, DRect box, double opacity = 1, DRect? crop = null)
    {
        if (!File.Exists(path))
            return;
        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };
        var data = Convert.ToBase64String(File.ReadAllBytes(path));
        if (crop is { } c && (c.Width < 1 || c.Height < 1))
        {
            // Scale the whole picture so the cropped part fills the box, and clip to the box.
            var fullW = box.Width / c.Width;
            var fullH = box.Height / c.Height;
            var id = "c" + Guid.NewGuid().ToString("N")[..8];
            _body.Append(Invariant($"<clipPath id=\"{id}\"><rect x=\"{box.X:0.##}\" y=\"{box.Y:0.##}\" width=\"{box.Width:0.##}\" height=\"{box.Height:0.##}\"/></clipPath>\n"));
            _body.Append(Invariant($"<image clip-path=\"url(#{id})\" x=\"{box.X - c.X * fullW:0.##}\" y=\"{box.Y - c.Y * fullH:0.##}\" width=\"{fullW:0.##}\" height=\"{fullH:0.##}\" opacity=\"{opacity:0.##}\" preserveAspectRatio=\"none\" href=\"data:{mime};base64,{data}\"/>\n"));
            return;
        }
        _body.Append(Invariant($"<image x=\"{box.X:0.##}\" y=\"{box.Y:0.##}\" width=\"{box.Width:0.##}\" height=\"{box.Height:0.##}\" opacity=\"{opacity:0.##}\" preserveAspectRatio=\"none\" href=\"data:{mime};base64,{data}\"/>\n"));
    }

    public IDisposable Rotate(DPoint center, double degrees)
    {
        _body.Append(Invariant($"<g transform=\"rotate({degrees:0.##} {center.X:0.##} {center.Y:0.##})\">\n"));
        _groups++;
        return new Closer(this);
    }

    public IDisposable Opacity(double opacity)
    {
        _body.Append(Invariant($"<g opacity=\"{opacity:0.##}\">\n"));
        _groups++;
        return new Closer(this);
    }

    private sealed class Closer(SvgSurface owner) : IDisposable
    {
        public void Dispose()
        {
            owner._body.Append("</g>\n");
            owner._groups--;
        }
    }

    private static List<string> Wrap(string text, DText style, double width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && AvaloniaSurface.MeasureText(candidate, style, double.PositiveInfinity).Width > width)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                    line = candidate;
            }
            lines.Add(line);
        }
        return lines;
    }

    private static string Paint(Color? fill, DPen? pen)
    {
        var sb = new StringBuilder();
        sb.Append(fill is { } f ? $"fill=\"{Hex(f)}\"" + (f.A < 255 ? Invariant($" fill-opacity=\"{f.A / 255.0:0.##}\"") : "") : "fill=\"none\"");
        if (pen is not null)
        {
            sb.Append(Invariant($" stroke=\"{Hex(pen.Color)}\" stroke-width=\"{pen.Thickness:0.##}\" stroke-linecap=\"round\" stroke-linejoin=\"round\""));
            if (pen.Dash == StrokeDash.Dashed)
                sb.Append(" stroke-dasharray=\"5 3\"");
            else if (pen.Dash == StrokeDash.Dotted)
                sb.Append(" stroke-dasharray=\"1 2.5\"");
        }
        return sb.ToString();
    }

    private static string Points(IEnumerable<DPoint> points) =>
        string.Join(' ', points.Select(p => Invariant($"{p.X:0.##},{p.Y:0.##}")));

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
