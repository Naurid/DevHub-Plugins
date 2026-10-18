using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DevHub.Diagrams.Core;
using DevHub.Diagrams.Rendering;

namespace DevHub.Diagrams.Controls;

/// <summary>The whole diagram small, with the visible part outlined; click or drag to look elsewhere.</summary>
public sealed class DiagramMinimap : Control
{
    public static readonly StyledProperty<IDiagramCanvasHost?> HostProperty =
        AvaloniaProperty.Register<DiagramMinimap, IDiagramCanvasHost?>(nameof(Host));

    private bool _dragging;

    static DiagramMinimap() => AffectsRender<DiagramMinimap>(HostProperty);

    public IDiagramCanvasHost? Host
    {
        get => GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HostProperty)
        {
            if (change.OldValue is IDiagramCanvasHost old)
                old.Redraw -= OnRedraw;
            if (change.NewValue is IDiagramCanvasHost host)
                host.Redraw += OnRedraw;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Host is { } host)
            host.Redraw -= OnRedraw;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnRedraw(object? sender, bool fit) => InvalidateVisual();

    private (DRect World, double Scale, double Dx, double Dy) Mapping(IDiagramCanvasHost host)
    {
        var view = new DRect(host.OffsetX, host.OffsetY, host.ViewportWidth / host.Zoom, host.ViewportHeight / host.Zoom);
        var world = host.Diagram.Nodes.Count == 0 ? view : host.Diagram.Bounds.Inflate(40).Union(view);
        var scale = Math.Min(Bounds.Width / Math.Max(1, world.Width), Bounds.Height / Math.Max(1, world.Height));
        var dx = (Bounds.Width - world.Width * scale) / 2;
        var dy = (Bounds.Height - world.Height * scale) / 2;
        return (world, scale, dx, dy);
    }

    public override void Render(DrawingContext context)
    {
        if (Host is not { } host || Bounds.Width < 4)
            return;
        var style = host.Style;
        context.FillRectangle(new SolidColorBrush(style.Background), new Rect(Bounds.Size));
        var (world, scale, dx, dy) = Mapping(host);
        using (context.PushTransform(Matrix.CreateTranslation(-world.X, -world.Y) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
        {
            var fill = new SolidColorBrush(style.MutedText, 0.5);
            foreach (var n in host.Diagram.Nodes)
                context.FillRectangle(host.Kind.Model.IsContainer(n) ? new SolidColorBrush(style.MutedText, 0.15) : fill,
                    new Rect(n.X, n.Y, Math.Max(2 / scale, n.Width), Math.Max(2 / scale, n.Height)));
            var view = new Rect(host.OffsetX, host.OffsetY, host.ViewportWidth / host.Zoom, host.ViewportHeight / host.Zoom);
            context.DrawRectangle(new SolidColorBrush(style.Accent, 0.1), new Pen(new SolidColorBrush(style.Accent), 1.5 / scale), view);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        e.Pointer.Capture(this);
        CenterOn(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
            CenterOn(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void CenterOn(Point p)
    {
        if (Host is not { } host)
            return;
        var (world, scale, dx, dy) = Mapping(host);
        var x = (p.X - dx) / scale + world.X;
        var y = (p.Y - dy) / scale + world.Y;
        host.OffsetX = x - host.ViewportWidth / host.Zoom / 2;
        host.OffsetY = y - host.ViewportHeight / host.Zoom / 2;
        InvalidateVisual();
    }
}
