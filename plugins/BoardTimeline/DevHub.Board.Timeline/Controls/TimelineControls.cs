using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DevHub.Controls;

/// <summary>Shared drawing for the timeline: theme lookups and redraw on theme change.</summary>
public abstract class TimelineControlBase : Control
{
    public static readonly StyledProperty<double> TodayProperty =
        AvaloniaProperty.Register<TimelineControlBase, double>(nameof(Today), -1);

    /// <summary>Where today falls across the range (0–1), or below 0 for "not shown".</summary>
    public double Today
    {
        get => GetValue(TodayProperty);
        set => SetValue(TodayProperty, value);
    }

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

    protected IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    protected double Size(string key, double fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is double d ? d : fallback;

    protected void DrawToday(DrawingContext context)
    {
        if (Today is < 0 or > 1)
            return;
        var x = Math.Round(Today * Bounds.Width) + 0.5;
        context.DrawLine(new Pen(Brush("Brush.Status.Error", Brushes.Red), 1), new Point(x, 0), new Point(x, Bounds.Height));
    }
}

/// <summary>
/// One roadmap bar (drawn in code): from <see cref="Start"/> to <see cref="End"/> (fractions of the row's width), filled
/// to <see cref="Progress"/>; red outline when late, dashed when its dates are worked out, a warning mark when it starts
/// before a blocker ends. Colors from the theme (epic color, status colors).
/// </summary>
public sealed class TimelineBar : TimelineControlBase
{
    public static readonly StyledProperty<double> StartProperty = AvaloniaProperty.Register<TimelineBar, double>(nameof(Start));
    public static readonly StyledProperty<double> EndProperty = AvaloniaProperty.Register<TimelineBar, double>(nameof(End));
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<TimelineBar, double>(nameof(Progress));
    public static readonly StyledProperty<bool> IsLateProperty = AvaloniaProperty.Register<TimelineBar, bool>(nameof(IsLate));
    public static readonly StyledProperty<bool> IsDoneProperty = AvaloniaProperty.Register<TimelineBar, bool>(nameof(IsDone));
    public static readonly StyledProperty<bool> IsGuessedProperty = AvaloniaProperty.Register<TimelineBar, bool>(nameof(IsGuessed));
    public static readonly StyledProperty<bool> HasConflictProperty = AvaloniaProperty.Register<TimelineBar, bool>(nameof(HasConflict));
    public static readonly StyledProperty<bool> IsChildProperty = AvaloniaProperty.Register<TimelineBar, bool>(nameof(IsChild));

    static TimelineBar()
    {
        AffectsRender<TimelineBar>(StartProperty, EndProperty, ProgressProperty, IsLateProperty, IsDoneProperty, IsGuessedProperty,
            HasConflictProperty, IsChildProperty, TodayProperty);
    }

    public double Start { get => GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public double End { get => GetValue(EndProperty); set => SetValue(EndProperty, value); }
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool IsLate { get => GetValue(IsLateProperty); set => SetValue(IsLateProperty, value); }
    public bool IsDone { get => GetValue(IsDoneProperty); set => SetValue(IsDoneProperty, value); }
    public bool IsGuessed { get => GetValue(IsGuessedProperty); set => SetValue(IsGuessedProperty, value); }
    public bool HasConflict { get => GetValue(HasConflictProperty); set => SetValue(HasConflictProperty, value); }
    public bool IsChild { get => GetValue(IsChildProperty); set => SetValue(IsChildProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, Size("Size.RowHeight", 24));

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        DrawToday(context);
        var left = Math.Clamp(Start, 0, 1) * w;
        var right = Math.Clamp(End, 0, 1) * w;
        if (right - left < 4)
            right = left + 4;
        var barHeight = IsChild ? h * 0.4 : h * 0.6;
        var rect = new Rect(left, (h - barHeight) / 2, right - left, barHeight);
        var radius = Size("Radius.Control", 4);
        var color = IsDone ? Brush("Brush.Status.Success", Brushes.Green) : Brush("Brush.IssueType.Epic", Brushes.MediumPurple);
        var track = new SolidColorBrush(((color as ISolidColorBrush)?.Color ?? Colors.Gray), 0.25);
        context.DrawRectangle(track, null, rect, radius, radius);
        if (Progress > 0)
            context.DrawRectangle(color, null, rect.WithWidth(Math.Max(radius, rect.Width * Math.Clamp(Progress, 0, 1))), radius, radius);
        if (IsLate || IsGuessed)
        {
            var pen = new Pen(IsLate ? Brush("Brush.Status.Error", Brushes.Red) : Brush("Brush.Text.Secondary", Brushes.Gray), 1.2,
                IsGuessed ? new DashStyle([3, 3], 0) : null);
            context.DrawRectangle(null, pen, rect.Deflate(0.6), radius, radius);
        }
        if (HasConflict)
        {
            var mark = Brush("Brush.Status.Warning", Brushes.Orange);
            context.DrawEllipse(mark, null, new Point(rect.Left - 7, h / 2), 4, 4);
        }
    }
}

/// <summary>The scale above the roadmap: month names (and week ticks) across the range, today's line.</summary>
public sealed class TimelineScale : TimelineControlBase
{
    public static readonly StyledProperty<DateTime> FromProperty = AvaloniaProperty.Register<TimelineScale, DateTime>(nameof(From));
    public static readonly StyledProperty<int> DaysProperty = AvaloniaProperty.Register<TimelineScale, int>(nameof(Days), 90);

    static TimelineScale()
    {
        AffectsRender<TimelineScale>(FromProperty, DaysProperty, TodayProperty);
    }

    public DateTime From { get => GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public int Days { get => GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, Size("Size.RowHeight", 24));

    public override void Render(DrawingContext context)
    {
        if (Days <= 0 || Bounds.Width <= 0)
            return;
        var w = Bounds.Width;
        var h = Bounds.Height;
        var text = Brush("Brush.Text.Secondary", Brushes.Gray);
        var line = new Pen(Brush("Brush.Separator", Brushes.LightGray), 1);
        var family = this.TryFindResource("Font.UI", out var f) && f is FontFamily font ? font : FontFamily.Default;
        var size = this.TryFindResource("FontSize.Small", out var s) && s is double d ? d : 12;
        var dayWidth = w / Days;
        double X(int dayIndex) => Math.Round(dayIndex * dayWidth) + 0.5;

        // A line at each month start (and where the range starts), week ticks on Mondays.
        var monthStarts = new List<int>();
        for (var i = 0; i < Days; i++)
        {
            var day = From.AddDays(i);
            if (day.Day == 1 || i == 0)
            {
                monthStarts.Add(i);
                context.DrawLine(line, new Point(X(i), 0), new Point(X(i), h));
            }
            else if (day.DayOfWeek == DayOfWeek.Monday && dayWidth * 7 > 24)
            {
                context.DrawLine(line, new Point(X(i), h * 0.7), new Point(X(i), h));
            }
        }

        // Each month's name, only where it fits before the next month (a few days of a month at the start of the
        // range would run into the next name) and inside the right edge.
        const double gap = 4;
        var labels = new List<Rect>();
        for (var m = 0; m < monthStarts.Count; m++)
        {
            var x = X(monthStarts[m]);
            var limit = m + 1 < monthStarts.Count ? X(monthStarts[m + 1]) : w;
            var label = new FormattedText(From.AddDays(monthStarts[m]).ToString(dayWidth * 30 > 90 ? "MMMM yyyy" : "MMM", CultureInfo.CurrentCulture),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(family), size, text);
            if (x + gap + label.Width + gap > limit)
                continue;
            var at = new Point(x + gap, (h - label.Height) / 2);
            context.DrawText(label, at);
            labels.Add(new Rect(at, new Size(label.Width, label.Height)));
        }

        // Today's line, starting below a month name it would cross.
        if (Today is >= 0 and <= 1)
        {
            var x = Math.Round(Today * w) + 0.5;
            var top = labels.Where(r => x >= r.Left - 1 && x <= r.Right + 1).Select(r => r.Bottom).DefaultIfEmpty(0).Max();
            context.DrawLine(new Pen(Brush("Brush.Status.Error", Brushes.Red), 1), new Point(x, top), new Point(x, h));
        }
    }
}
