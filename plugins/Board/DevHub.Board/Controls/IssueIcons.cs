using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DevHub.Controls;

/// <summary>
/// The small colored square of an issue type (epic, story, task, bug, sub-task, or any other name), drawn in code so
/// it follows the theme tokens (<c>Brush.IssueType.*</c>). Accessible name: the type's name, set by the view.
/// </summary>
public sealed class IssueTypeIcon : Control
{
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<IssueTypeIcon, string?>(nameof(Kind));

    static IssueTypeIcon()
    {
        AffectsRender<IssueTypeIcon>(KindProperty);
    }

    /// <summary>The issue type's icon name (<c>IssueType.Icon</c>).</summary>
    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    // Colors come from the theme: draw again when it switches (subscribed only while on screen).
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Resource("Size.IssueTypeIcon") is double d ? d : 16;
        return new Size(size, size);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;
        var (brushKey, glyph) = (Kind ?? "").ToLowerInvariant() switch
        {
            "epic" => ("Brush.IssueType.Epic", Glyph.Bolt),
            "story" => ("Brush.IssueType.Story", Glyph.Bookmark),
            "task" => ("Brush.IssueType.Task", Glyph.Check),
            "bug" => ("Brush.IssueType.Bug", Glyph.Dot),
            "subtask" => ("Brush.IssueType.Subtask", Glyph.Split),
            "spike" => ("Brush.IssueType.Other", Glyph.Bolt),
            _ => ("Brush.IssueType.Other", Glyph.Check),
        };
        var background = Resource(brushKey) as IBrush ?? Brushes.Gray;
        var foreground = Resource("Brush.IssueType.Glyph") as IBrush ?? Brushes.White;
        var box = new Rect(0, 0, size, size);
        context.DrawRectangle(background, null, box, size * 0.2, size * 0.2);

        var s = size;
        var pen = new Pen(foreground, Math.Max(1.2, s * 0.11), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        switch (glyph)
        {
            case Glyph.Bolt:
                context.DrawGeometry(foreground, null, Polygon(s, (0.56, 0.18), (0.30, 0.56), (0.48, 0.56), (0.42, 0.82), (0.70, 0.44), (0.52, 0.44)));
                break;
            case Glyph.Bookmark:
                context.DrawGeometry(foreground, null, Polygon(s, (0.32, 0.22), (0.68, 0.22), (0.68, 0.80), (0.50, 0.64), (0.32, 0.80)));
                break;
            case Glyph.Check:
                context.DrawLine(pen, P(s, 0.28, 0.52), P(s, 0.44, 0.68));
                context.DrawLine(pen, P(s, 0.44, 0.68), P(s, 0.74, 0.34));
                break;
            case Glyph.Dot:
                context.DrawEllipse(foreground, null, P(s, 0.5, 0.5), s * 0.2, s * 0.2);
                break;
            case Glyph.Split:
                context.DrawRectangle(null, pen, new Rect(s * 0.26, s * 0.26, s * 0.3, s * 0.3));
                context.DrawRectangle(foreground, null, new Rect(s * 0.46, s * 0.46, s * 0.3, s * 0.3));
                break;
        }
    }

    private enum Glyph { Bolt, Bookmark, Check, Dot, Split }

    private static Point P(double size, double x, double y) => new(size * x, size * y);

    private static StreamGeometry Polygon(double size, params (double X, double Y)[] points)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(P(size, points[0].X, points[0].Y), true);
        foreach (var (x, y) in points.Skip(1))
            ctx.LineTo(P(size, x, y));
        ctx.EndFigure(true);
        return geometry;
    }

    private object? Resource(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value : null;
}

/// <summary>A priority's arrow(s) (<c>Brush.Priority.*</c>): two up, one up, a bar, one down, two down.</summary>
public sealed class PriorityIcon : Control
{
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<PriorityIcon, string?>(nameof(Kind));

    static PriorityIcon()
    {
        AffectsRender<PriorityIcon>(KindProperty);
    }

    /// <summary>The priority's icon name (<c>Priority.Icon</c>): highest, high, medium, low, lowest.</summary>
    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    // Colors come from the theme: draw again when it switches (subscribed only while on screen).
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = this.TryFindResource("Size.IssueTypeIcon", ActualThemeVariant, out var v) && v is double d ? d : 16;
        return new Size(size, size);
    }

    public override void Render(DrawingContext context)
    {
        var s = Math.Min(Bounds.Width, Bounds.Height);
        if (s <= 0 || string.IsNullOrEmpty(Kind))
            return;
        var key = Kind.ToLowerInvariant() switch
        {
            "highest" => "Brush.Priority.Highest",
            "high" => "Brush.Priority.High",
            "low" => "Brush.Priority.Low",
            "lowest" => "Brush.Priority.Lowest",
            _ => "Brush.Priority.Medium",
        };
        var brush = this.TryFindResource(key, ActualThemeVariant, out var b) && b is IBrush ib ? ib : Brushes.Orange;
        var pen = new Pen(brush, Math.Max(1.4, s * 0.13), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        void Chevron(double y, bool up)
        {
            var dy = up ? -0.14 : 0.14;
            context.DrawLine(pen, new Point(s * 0.24, s * (y - dy)), new Point(s * 0.5, s * (y + dy)));
            context.DrawLine(pen, new Point(s * 0.5, s * (y + dy)), new Point(s * 0.76, s * (y - dy)));
        }

        switch (Kind.ToLowerInvariant())
        {
            case "highest":
                Chevron(0.36, true);
                Chevron(0.62, true);
                break;
            case "high":
                Chevron(0.5, true);
                break;
            case "low":
                Chevron(0.5, false);
                break;
            case "lowest":
                Chevron(0.38, false);
                Chevron(0.64, false);
                break;
            default:
                context.DrawLine(pen, new Point(s * 0.24, s * 0.4), new Point(s * 0.76, s * 0.4));
                context.DrawLine(pen, new Point(s * 0.24, s * 0.6), new Point(s * 0.76, s * 0.6));
                break;
        }
    }
}
