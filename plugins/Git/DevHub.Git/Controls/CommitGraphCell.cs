using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DevHub.Git.Core;

namespace DevHub.Controls;

/// <summary>
/// Draws one row of the commit graph: the lines crossing the row and the commit's dot, from a
/// <see cref="GraphRow"/> laid out by <see cref="CommitGraph"/> (Core). Rows sit edge to edge in
/// the list, so the half-row segments join into continuous lines.
/// <c>&lt;c:CommitGraphCell Row="{Binding Graph}" Lanes="{Binding GraphLanes}" /&gt;</c>
/// </summary>
/// <remarks>
/// Drawn in code (like a Unity OnGUI / Gizmos pass) instead of with shapes in XAML: a row can
/// have a dozen segments, and a list of thousands of rows would otherwise build tens of
/// thousands of controls. Colors come from the <c>Brush.Graph.0…7</c> tokens.
/// </remarks>
public class CommitGraphCell : Control
{
    private const int PaletteSize = 8;

    public static readonly StyledProperty<GraphRow?> RowProperty =
        AvaloniaProperty.Register<CommitGraphCell, GraphRow?>(nameof(Row));

    /// <summary>How many lanes wide the cell is (the same for every row, so the columns after it line up).</summary>
    public static readonly StyledProperty<int> LanesProperty =
        AvaloniaProperty.Register<CommitGraphCell, int>(nameof(Lanes), 1);

    public static readonly StyledProperty<double> LaneWidthProperty =
        AvaloniaProperty.Register<CommitGraphCell, double>(nameof(LaneWidth), 14);

    public static readonly StyledProperty<double> DotSizeProperty =
        AvaloniaProperty.Register<CommitGraphCell, double>(nameof(DotSize), 8);

    public static readonly StyledProperty<double> LineThicknessProperty =
        AvaloniaProperty.Register<CommitGraphCell, double>(nameof(LineThickness), 1.5);

    /// <summary>Draw the dot with a ring (the checked-out commit).</summary>
    public static readonly StyledProperty<bool> IsHeadProperty =
        AvaloniaProperty.Register<CommitGraphCell, bool>(nameof(IsHead));

    static CommitGraphCell()
    {
        AffectsRender<CommitGraphCell>(RowProperty, LaneWidthProperty, DotSizeProperty, LineThicknessProperty, IsHeadProperty);
        AffectsMeasure<CommitGraphCell>(LanesProperty, LaneWidthProperty);
        ClipToBoundsProperty.OverrideDefaultValue<CommitGraphCell>(true);
    }

    // The brushes change color with the theme: redraw when it switches (subscribed only while on screen).
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

    public GraphRow? Row { get => GetValue(RowProperty); set => SetValue(RowProperty, value); }
    public int Lanes { get => GetValue(LanesProperty); set => SetValue(LanesProperty, value); }
    public double LaneWidth { get => GetValue(LaneWidthProperty); set => SetValue(LaneWidthProperty, value); }
    public double DotSize { get => GetValue(DotSizeProperty); set => SetValue(DotSizeProperty, value); }
    public double LineThickness { get => GetValue(LineThicknessProperty); set => SetValue(LineThicknessProperty, value); }
    public bool IsHead { get => GetValue(IsHeadProperty); set => SetValue(IsHeadProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Math.Max(1, Lanes) * LaneWidth, 0);

    public override void Render(DrawingContext context)
    {
        if (Row is not { } row)
            return;

        var height = Bounds.Height;
        var middle = height / 2;

        foreach (var edge in row.Edges)
        {
            var (y0, y1) = edge.Part == GraphEdgePart.Top ? (0d, middle) : (middle, height);
            var pen = new Pen(BrushFor(edge.Color), LineThickness, lineCap: PenLineCap.Round);
            var start = new Point(X(edge.From), y0);
            var end = new Point(X(edge.To), y1);

            if (edge.From == edge.To)
            {
                context.DrawLine(pen, start, end);
                continue;
            }

            // A bend from one lane to another: an S-curve within the half row.
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var bend = (y0 + y1) / 2;
                g.BeginFigure(start, isFilled: false);
                g.CubicBezierTo(new Point(start.X, bend), new Point(end.X, bend), end);
                g.EndFigure(isClosed: false);
            }
            context.DrawGeometry(null, pen, geometry);
        }

        var center = new Point(X(row.Lane), middle);
        var brush = BrushFor(row.Color);
        var radius = DotSize / 2;
        if (IsHead)
        {
            // Checked-out commit: a ring around the dot.
            context.DrawEllipse(null, new Pen(brush, LineThickness), center, radius + LineThickness * 1.5, radius + LineThickness * 1.5);
        }
        context.DrawEllipse(brush, null, center, radius, radius);
    }

    private double X(int lane) => lane * LaneWidth + LaneWidth / 2;

    private IBrush BrushFor(int color) =>
        this.TryFindResource($"Brush.Graph.{color % PaletteSize}", ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Gray;
}
