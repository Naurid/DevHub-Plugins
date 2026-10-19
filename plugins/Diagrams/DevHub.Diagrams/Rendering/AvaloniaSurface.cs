using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DevHub.Diagrams.Core;

namespace DevHub.Diagrams.Rendering;

/// <summary>Draws on an Avalonia <see cref="DrawingContext"/> (the canvas, and PNG exports through a render target).</summary>
public sealed class AvaloniaSurface(DrawingContext context, DiagramStyle style, string? folder = null) : IDiagramSurface
{
    public DiagramStyle Style { get; } = style;

    public string? Folder => folder;

    public void Rect(DRect r, Color? fill, DPen? pen, double radius = 0) =>
        context.DrawRectangle(Brush(fill), Pen(pen), new Rect(r.X, r.Y, r.Width, r.Height), radius, radius);

    public void Ellipse(DRect r, Color? fill, DPen? pen) =>
        context.DrawEllipse(Brush(fill), Pen(pen), new Point(r.Center.X, r.Center.Y), r.Width / 2, r.Height / 2);

    public void Polygon(IReadOnlyList<DPoint> points, Color? fill, DPen? pen)
    {
        if (points.Count < 2)
            return;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(P(points[0]), fill is not null);
            foreach (var p in points.Skip(1))
                g.LineTo(P(p));
            g.EndFigure(true);
        }
        context.DrawGeometry(Brush(fill), Pen(pen), geometry);
    }

    public void Polyline(IReadOnlyList<DPoint> points, DPen pen)
    {
        if (points.Count < 2)
            return;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(P(points[0]), false);
            foreach (var p in points.Skip(1))
                g.LineTo(P(p));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, Pen(pen), geometry);
    }

    public void Line(DPoint a, DPoint b, DPen pen) => context.DrawLine(Pen(pen)!, P(a), P(b));

    public void Curve(IReadOnlyList<DPoint> points, DPen pen)
    {
        if (points.Count < 3)
        {
            Polyline(points, pen);
            return;
        }
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(P(points[0]), false);
            // Through the midpoints, with the points as control points: smooth, and it never overshoots much.
            for (var i = 1; i < points.Count - 1; i++)
            {
                var mid = points[i] + (points[i + 1] - points[i]) * 0.5;
                g.QuadraticBezierTo(P(points[i]), P(mid));
            }
            g.LineTo(P(points[^1]));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, Pen(pen), geometry);
    }

    public void Text(string text, DRect box, DText style, bool wrap = true)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var formatted = Format(text, style, Style, wrap ? Math.Max(1, box.Width) : double.PositiveInfinity, leftAligned: !wrap);
        var width = wrap ? box.Width : formatted.Width;
        var x = wrap ? box.X : style.Align switch
        {
            TextAlign.Left => box.X,
            TextAlign.Right => box.Right - formatted.Width,
            _ => box.Center.X - formatted.Width / 2,
        };
        var y = style.Vertical switch
        {
            TextVertical.Top => box.Y,
            TextVertical.Bottom => box.Bottom - formatted.Height,
            _ => box.Center.Y - formatted.Height / 2,
        };
        _ = width;
        context.DrawText(formatted, new Point(x, y));
    }

    public (double Width, double Height) Measure(string text, DText style, double maxWidth = double.PositiveInfinity) =>
        MeasureText(text, style, maxWidth);

    public void Image(string path, DRect box, double opacity = 1, DRect? crop = null)
    {
        var bitmap = ImageCache.Get(path);
        if (bitmap is null)
        {
            Rect(box, Style.ContainerFill, new DPen(Style.MutedText, 1, StrokeDash.Dashed));
            Text(System.IO.Path.GetFileName(path), box.Inflate(-6), new DText(11, Color: Style.MutedText));
            return;
        }
        var size = bitmap.Size;
        var source = crop is { } c
            ? new Rect(c.X * size.Width, c.Y * size.Height, Math.Max(1, c.Width * size.Width), Math.Max(1, c.Height * size.Height))
            : new Rect(size);
        using (context.PushOpacity(opacity))
            context.DrawImage(bitmap, source, new Rect(box.X, box.Y, box.Width, box.Height));
    }

    public IDisposable Rotate(DPoint center, double degrees)
    {
        if (degrees == 0)
            return Nothing.Instance;
        var m = Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateRotation(degrees * Math.PI / 180)
                * Matrix.CreateTranslation(center.X, center.Y);
        return context.PushTransform(m);
    }

    public IDisposable Opacity(double opacity) => context.PushOpacity(opacity);

    private static Point P(DPoint p) => new(p.X, p.Y);

    private static IBrush? Brush(Color? c) => c is { } color ? new SolidColorBrush(color) : null;

    private static IPen? Pen(DPen? p) => p is null ? null : new Pen(new SolidColorBrush(p.Color), p.Thickness,
        p.Dash switch
        {
            StrokeDash.Dashed => new DashStyle([5, 3], 0),
            StrokeDash.Dotted => new DashStyle([1, 2.5], 0),
            _ => null,
        }, PenLineCap.Round, PenLineJoin.Round);

    internal static FontFamily UiFont { get; } =
        Avalonia.Application.Current?.TryGetResource("Font.UI", null, out var f) == true && f is FontFamily family ? family : FontFamily.Default;

    private static readonly FontFamily MonoFont = new("JetBrains Mono, Cascadia Mono, Consolas, monospace");

    /// <summary>
    /// A text laid out, from a cache shared by every surface: the canvas draws every shape's text on every frame (pan,
    /// zoom, drag), and laying the same texts out again each time was most of a frame's work. Same text, style, width
    /// and colour: the same laid-out text. Used on the UI thread only (the canvas and image exports).
    /// </summary>
    internal static FormattedText Format(string text, DText style, DiagramStyle colours, double maxWidth, bool leftAligned = false)
    {
        var color = style.Color ?? colours.Text;
        var key = (text, style, color, double.IsInfinity(maxWidth) ? -1 : Math.Round(maxWidth, 1), leftAligned);
        if (Layouts.TryGetValue(key, out var cached))
            return cached;
        if (Layouts.Count >= MaxLayouts)
            Layouts.Clear();   // a big diagram, or the theme changed: start over rather than track ages

        var typeface = new Typeface(style.Mono ? MonoFont : UiFont, style.Italic ? FontStyle.Italic : FontStyle.Normal,
            style.Bold ? FontWeight.SemiBold : FontWeight.Normal);
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, style.Size,
            new SolidColorBrush(color))
        {
            TextAlignment = leftAligned ? TextAlignment.Left
                : style.Align switch { TextAlign.Left => TextAlignment.Left, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Center },
        };
        if (!double.IsInfinity(maxWidth))
            formatted.MaxTextWidth = maxWidth;
        if (style.Underline)
            formatted.SetTextDecorations(TextDecorations.Underline);
        Layouts[key] = formatted;
        return formatted;
    }

    private const int MaxLayouts = 4000;
    private static readonly Dictionary<(string, DText, Color, double, bool), FormattedText> Layouts = new();

    internal static (double Width, double Height) MeasureText(string text, DText style, double maxWidth)
    {
        if (string.IsNullOrEmpty(text))
            return (0, style.Size * 1.3);
        var f = Format(text, style, DiagramStyle.Light, maxWidth);
        return (f.WidthIncludingTrailingWhitespace, f.Height);
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}

/// <summary>Pictures for mood boards, loaded once (again when the file changes), at most a few dozen kept.</summary>
public static class ImageCache
{
    private static readonly Dictionary<string, (DateTime Written, Bitmap? Bitmap)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    public static Bitmap? Get(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var written = File.GetLastWriteTimeUtc(path);
            lock (Gate)
            {
                if (Cache.TryGetValue(path, out var hit) && hit.Written == written)
                    return hit.Bitmap;
                Bitmap? bitmap;
                try
                {
                    using var stream = File.OpenRead(path);
                    bitmap = new Bitmap(stream);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    bitmap = null;   // not a picture we can read: drawn as a placeholder
                }
                if (Cache.Count > 64)
                    Cache.Clear();
                Cache[path] = (written, bitmap);
                return bitmap;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The picture's size in pixels, or null.</summary>
    public static (double Width, double Height)? SizeOf(string path) => Get(path) is { } b ? (b.Size.Width, b.Size.Height) : null;
}
