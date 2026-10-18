using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DevHub.Diagrams.Core;
using DevHub.Diagrams.Rendering;
using DevHub.Integration;

namespace DevHub.Diagrams.Controls;

/// <summary>What a press on the canvas does.</summary>
public enum CanvasTool { Select, Pan, Node, Edge, Pen, Eraser }

/// <summary>
/// The open diagram as the canvas drives it (the document ViewModel): view state (zoom, scroll, tool, grid) and the
/// actions the canvas asks for. The canvas only draws and turns pointer gestures into these calls and operations.
/// </summary>
public interface IDiagramCanvasHost : IDiagramEditor
{
    double Zoom { get; set; }
    double OffsetX { get; set; }
    double OffsetY { get; set; }

    CanvasTool Tool { get; set; }

    /// <summary>The node or edge type the Node / Edge tool makes.</summary>
    string? ToolType { get; }

    /// <summary>The line type the connect handle makes (the kind's first, or the one picked last).</summary>
    string? DefaultEdgeType { get; }

    double GridSize { get; }
    bool ShowGrid { get; }
    bool SnapToGrid { get; }

    /// <summary>Pen colour and width (mood boards).</summary>
    string PenColor { get; }
    double PenWidth { get; }

    DiagramStyle Style { get; }

    /// <summary>The linked issue of a key (cached; null = unknown or no Board).</summary>
    IssueLinkInfo? Issue(string key);

    /// <summary>Something changed that the canvas must redraw (or, true, also re-fit the view).</summary>
    event EventHandler<bool>? Redraw;

    /// <summary>Asks the canvas to edit a shape's or a line's text in place.</summary>
    event EventHandler<string>? EditTextRequested;

    void CreateNode(string type, DPoint center);

    void Connect(string edgeType, string fromId, string toId);

    void AddStroke(IReadOnlyList<DPoint> points);

    Task DropFilesAsync(IReadOnlyList<string> paths, DPoint at);

    /// <summary>The visible area changed size (Fit and View Center need it).</summary>
    void ViewportChanged(double width, double height);

    /// <summary>The canvas's size in pixels (the minimap outlines what's visible).</summary>
    double ViewportWidth { get; }

    double ViewportHeight { get; }
}

/// <summary>
/// The diagram editor's drawing area: pan (Space or middle button), zoom (Ctrl+wheel), select (click, Ctrl+click,
/// rubber band), move (snaps to the grid and to other shapes' edges), resize (handles), connect (the ● handle, or the
/// line tool), bend lines (drag a line's handles), draw (pen), edit text in place (double-click, F2). Changes go to
/// the host as operations, one per gesture, so each is one Undo.
/// </summary>
public sealed class DiagramCanvas : Control
{
    public static readonly StyledProperty<IDiagramCanvasHost?> HostProperty =
        AvaloniaProperty.Register<DiagramCanvas, IDiagramCanvasHost?>(nameof(Host));

    private enum Drag { None, Pan, Move, Resize, Rubber, Connect, Waypoint, Pen }

    private const double HandleSize = 7;

    private Drag _drag;
    private Point _pressScreen;
    private DPoint _pressWorld;
    private DPoint _pointerWorld;
    private List<string> _moving = [];
    private DPoint _applied;
    private int _handle;
    private DRect _originalBounds;
    private string? _connectFrom;
    private string? _connectType;
    private string? _waypointEdge;
    private List<DPoint> _originalWaypoints = [];
    private int _waypointIndex;
    private readonly List<DPoint> _stroke = [];
    private double? _guideX, _guideY;
    private string? _hover;
    private bool _space;
    private bool _fitPending = true;
    private readonly TextBox _editor = new() { IsVisible = false, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, MinWidth = 60 };
    private string? _editing;

    static DiagramCanvas()
    {
        AffectsRender<DiagramCanvas>(HostProperty);
        FocusableProperty.OverrideDefaultValue<DiagramCanvas>(true);
    }

    public DiagramCanvas()
    {
        ClipToBounds = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        _editor.KeyDown += OnEditorKeyDown;
        _editor.LostFocus += (_, _) => CommitEdit();
        LogicalChildren.Add(_editor);
        VisualChildren.Add(_editor);
    }

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
            {
                old.Redraw -= OnRedraw;
                old.EditTextRequested -= OnEditTextRequested;
            }
            if (change.NewValue is IDiagramCanvasHost host)
            {
                host.Redraw += OnRedraw;
                host.EditTextRequested += OnEditTextRequested;
                _fitPending = true;
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Host is { } host)
        {
            host.Redraw -= OnRedraw;
            host.EditTextRequested -= OnEditTextRequested;
        }
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Host is { } host)
        {
            host.Redraw -= OnRedraw;
            host.Redraw += OnRedraw;
            host.EditTextRequested -= OnEditTextRequested;
            host.EditTextRequested += OnEditTextRequested;
        }
    }

    private void OnRedraw(object? sender, bool fit)
    {
        if (fit)
            _fitPending = true;
        PlaceEditor();
        InvalidateVisual();
        InvalidateArrange();
    }

    // ---- Coordinates ----

    private double Zoom => Host?.Zoom ?? 1;

    private DPoint ToWorld(Point p) => new(p.X / Zoom + (Host?.OffsetX ?? 0), p.Y / Zoom + (Host?.OffsetY ?? 0));

    private Point ToScreen(DPoint p) => new((p.X - (Host?.OffsetX ?? 0)) * Zoom, (p.Y - (Host?.OffsetY ?? 0)) * Zoom);

    private DPoint Snap(DPoint p) => Host is { SnapToGrid: true } h ? Layout.Snap(p, h.GridSize) : p;

    private Func<DiagramNode, NodeShape> ShapeOf => n => Host!.Kind.Model.ShapeOf(n);

    private IReadOnlyList<DPoint> PathOf(DiagramEdge e) => Host!.Kind.Model.PathOf(Host.Diagram, e);

    /// <summary>Shows the whole diagram (or 100% around its top-left when it's small).</summary>
    public void Fit()
    {
        if (Host is not { } host || Bounds.Width < 10 || Bounds.Height < 10)
            return;
        var area = host.Diagram.Bounds;
        if (area.Width <= 0 || area.Height <= 0)
        {
            host.Zoom = 1;
            host.OffsetX = -40;
            host.OffsetY = -40;
        }
        else
        {
            area = area.Inflate(40);
            var zoom = Math.Clamp(Math.Min(Bounds.Width / area.Width, Bounds.Height / area.Height), 0.2, 1);
            host.Zoom = zoom;
            host.OffsetX = area.Center.X - Bounds.Width / zoom / 2;
            host.OffsetY = area.Center.Y - Bounds.Height / zoom / 2;
        }
        InvalidateVisual();
    }

    // ---- Layout of the in-place text box ----

    protected override Size ArrangeOverride(Size finalSize)
    {
        Host?.ViewportChanged(finalSize.Width, finalSize.Height);
        if (_fitPending && finalSize.Width > 10 && Host is not null)
        {
            _fitPending = false;
            Post(Fit);
        }
        PlaceEditor();
        return finalSize;
    }

    private static void Post(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);

    private void PlaceEditor()
    {
        if (_editing is null || Host is not { } host)
        {
            _editor.Arrange(default);
            return;
        }
        var rect = EditRect(host.Diagram, _editing);
        var topLeft = ToScreen(new DPoint(rect.X, rect.Y));
        var size = new Size(Math.Max(80, rect.Width * Zoom), Math.Max(28, rect.Height * Zoom));
        _editor.Measure(size);
        _editor.Arrange(new Rect(topLeft, size));
    }

    private DRect EditRect(Diagram diagram, string id)
    {
        if (diagram.Node(id) is { } n)
            return Host!.Kind.MultilineText(n) ? n.Bounds : n.Bounds with { Y = n.Center.Y - 16, Height = 32 };
        if (diagram.Edge(id) is { } e)
        {
            var at = Routing.LabelPoint(PathOf(e), e.LabelAt);
            return new DRect(at.X - 70, at.Y - 14, 140, 28);
        }
        return default;
    }

    private void OnEditTextRequested(object? sender, string id) => StartEdit(id);

    private void StartEdit(string id)
    {
        if (Host is not { } host)
            return;
        var text = host.Diagram.Node(id)?.Text ?? host.Diagram.Edge(id)?.Text;
        if (text is null)
            return;
        _editing = id;
        _editor.AcceptsReturn = host.Diagram.Node(id) is { } n && host.Kind.MultilineText(n);
        _editor.Text = text;
        _editor.IsVisible = true;
        PlaceEditor();
        _editor.Focus();
        _editor.SelectAll();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _editing = null;
            _editor.IsVisible = false;
            Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (!_editor.AcceptsReturn || e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            CommitEdit();
            Focus();
            e.Handled = true;
        }
    }

    private void CommitEdit()
    {
        if (_editing is not { } id || Host is not { } host)
            return;
        _editing = null;
        _editor.IsVisible = false;
        var text = _editor.Text ?? "";
        var old = host.Diagram.Node(id)?.Text ?? host.Diagram.Edge(id)?.Text;
        if (old is not null && old != text)
            host.Do(new SetText(id, text));
        InvalidateVisual();
    }

    // ---- Drawing ----

    public override void Render(DrawingContext context)
    {
        if (Host is not { } host)
            return;
        var style = host.Style;
        context.FillRectangle(new SolidColorBrush(style.Background), new Rect(Bounds.Size));
        if (host.ShowGrid)
            DrawGrid(context, host, style);

        using (context.PushTransform(Matrix.CreateTranslation(-host.OffsetX, -host.OffsetY) * Matrix.CreateScale(Zoom, Zoom)))
        {
            var surface = new AvaloniaSurface(context, style, host.FilePath is { } file ? Path.GetDirectoryName(file) : null);
            var hidden = _editing is { } editing ? new HashSet<string>() : null;
            DiagramRenderer.Draw(surface, host.Diagram, host.Kind, host.Issue, hidden);
            DrawOverlay(context, surface, host, style);
        }

        if (_drag == Drag.Rubber)
        {
            var r = DRect.FromCorners(new DPoint(_pressScreen.X, _pressScreen.Y), new DPoint(ToScreen(_pointerWorld).X, ToScreen(_pointerWorld).Y));
            var accent = style.Selection;
            context.DrawRectangle(new SolidColorBrush(accent, 0.12), new Pen(new SolidColorBrush(accent), 1), new Rect(r.X, r.Y, r.Width, r.Height));
        }
    }

    private void DrawGrid(DrawingContext context, IDiagramCanvasHost host, DiagramStyle style)
    {
        var step = host.GridSize;
        while (step * Zoom < 12)
            step *= 2;
        var pen = new Pen(new SolidColorBrush(style.Grid), 1);
        var topLeft = ToWorld(new Point(0, 0));
        var bottomRight = ToWorld(new Point(Bounds.Width, Bounds.Height));
        for (var x = Math.Floor(topLeft.X / step) * step; x <= bottomRight.X; x += step)
        {
            var sx = Math.Round(ToScreen(new DPoint(x, 0)).X) + 0.5;
            context.DrawLine(pen, new Point(sx, 0), new Point(sx, Bounds.Height));
        }
        for (var y = Math.Floor(topLeft.Y / step) * step; y <= bottomRight.Y; y += step)
        {
            var sy = Math.Round(ToScreen(new DPoint(0, y)).Y) + 0.5;
            context.DrawLine(pen, new Point(0, sy), new Point(Bounds.Width, sy));
        }
    }

    private void DrawOverlay(DrawingContext context, AvaloniaSurface s, IDiagramCanvasHost host, DiagramStyle style)
    {
        var diagram = host.Diagram;
        var accent = style.Selection;
        var thin = 1 / Zoom;
        var selectionPen = new DPen(accent, 1.5 * thin, StrokeDash.Dashed);
        var selected = host.Selection.ToHashSet();

        // Hovered target while connecting or with the line tool.
        if ((_drag == Drag.Connect || host.Tool == CanvasTool.Edge) && _hover is { } hoverId && diagram.Node(hoverId) is { } hovered)
            s.Rect(hovered.Bounds.Inflate(4 * thin), null, new DPen(accent, 2 * thin), 4 * thin);

        foreach (var n in diagram.Nodes.Where(n => selected.Contains(n.Id)))
            s.Rect(n.Bounds.Inflate(3 * thin), null, selectionPen, 3 * thin);

        foreach (var e in diagram.Edges.Where(e => selected.Contains(e.Id)))
        {
            var path = PathOf(e);
            s.Polyline(path, new DPen(Color.FromArgb(90, accent.R, accent.G, accent.B), 6 * thin));
            foreach (var w in e.Waypoints)
                s.Ellipse(Square(w, HandleSize * thin), style.Background, new DPen(accent, 1.5 * thin));
            for (var i = 1; i < path.Count; i++)
                s.Ellipse(Square(path[i - 1] + (path[i] - path[i - 1]) * 0.5, 5 * thin), accent, null);
        }

        if (SingleNode(host) is { } single)
        {
            foreach (var h in Handles(single.Bounds))
                s.Rect(Square(h, HandleSize * thin), style.Background, new DPen(accent, 1.2 * thin));
            if (Connectable(host, single))
            {
                var c = ConnectHandle(single.Bounds);
                s.Ellipse(Square(c, 10 * thin), accent, new DPen(style.Background, 1.5 * thin));
            }
        }

        if (_guideX is { } gx)
            s.Line(new DPoint(gx, -100000), new DPoint(gx, 100000), new DPen(Color.Parse("#E5484D"), thin));
        if (_guideY is { } gy)
            s.Line(new DPoint(-100000, gy), new DPoint(100000, gy), new DPen(Color.Parse("#E5484D"), thin));

        if (_drag == Drag.Connect && _connectFrom is { } fromId && diagram.Node(fromId) is { } from)
            s.Line(from.Center, _pointerWorld, new DPen(accent, 1.5 * thin, StrokeDash.Dashed));

        if (_drag == Drag.Pen && _stroke.Count > 1)
            s.Curve(_stroke, new DPen(Color.Parse(host.PenColor), host.PenWidth));
    }

    private static DRect Square(DPoint c, double size) => new(c.X - size / 2, c.Y - size / 2, size, size);

    private DPoint ConnectHandle(DRect r) => new(r.Right + 14 / Zoom, r.Center.Y);

    private static bool Connectable(IDiagramCanvasHost host, DiagramNode n) =>
        host.DefaultEdgeType is not null && host.Kind.Model.NodeType(n.Type)?.Connectable != false;

    // Corners and side middles, clockwise from the top-left.
    private static DPoint[] Handles(DRect r) =>
    [
        new(r.Left, r.Top), new(r.Center.X, r.Top), new(r.Right, r.Top), new(r.Right, r.Center.Y),
        new(r.Right, r.Bottom), new(r.Center.X, r.Bottom), new(r.Left, r.Bottom), new(r.Left, r.Center.Y),
    ];

    private static DiagramNode? SingleNode(IDiagramCanvasHost host) =>
        host.Selection.Count == 1 ? host.Diagram.Node(host.Selection[0]) : null;

    // ---- Pointer ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Host is not { } host)
            return;
        if (_editing is not null)
            CommitEdit();
        Focus();
        var point = e.GetCurrentPoint(this);
        _pressScreen = point.Position;
        _pressWorld = ToWorld(point.Position);
        _pointerWorld = _pressWorld;
        var diagram = host.Diagram;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (point.Properties.IsMiddleButtonPressed || (point.Properties.IsLeftButtonPressed && (host.Tool == CanvasTool.Pan || _space)))
        {
            Start(Drag.Pan, e);
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            // Right-click selects what's under the pointer (the context menu acts on it), then opens the menu.
            var hit = HitAt(host, _pressWorld);
            var under = hit.Node?.Id ?? hit.Edge?.Id;
            if (under is not null && !host.Selection.Contains(under))
                host.Select([under]);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
        {
            var hit = HitAt(host, _pressWorld);
            var target = hit.Node?.Id ?? hit.Edge?.Id;
            if (target is not null)
            {
                host.Select([target]);
                StartEdit(target);
                e.Handled = true;
            }
            return;
        }

        switch (host.Tool)
        {
            case CanvasTool.Node when host.ToolType is { } type:
                host.CreateNode(type, Snap(_pressWorld));
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    host.Tool = CanvasTool.Select;
                e.Handled = true;
                return;
            case CanvasTool.Edge when host.ToolType is { } edgeType:
                if (Layout.NodeAt(diagram, _pressWorld, ShapeOf) is { } source)
                {
                    _connectFrom = source.Id;
                    _connectType = edgeType;
                    Start(Drag.Connect, e);
                }
                return;
            case CanvasTool.Pen:
                _stroke.Clear();
                _stroke.Add(_pressWorld);
                Start(Drag.Pen, e);
                return;
            case CanvasTool.Eraser:
                if (Layout.NodeAt(diagram, _pressWorld, ShapeOf, n => ShapeOf(n) != NodeShape.Path) is { } stroke)
                    host.Do(new RemoveElements([stroke.Id]));
                e.Handled = true;
                return;
        }

        // Select tool: handles of the selection first.
        if (SingleNode(host) is { } single)
        {
            if (Connectable(host, single) && ConnectHandle(single.Bounds).DistanceTo(_pressWorld) <= 8 / Zoom)
            {
                _connectFrom = single.Id;
                _connectType = host.DefaultEdgeType;
                Start(Drag.Connect, e);
                return;
            }
            var handles = Handles(single.Bounds);
            for (var i = 0; i < handles.Length; i++)
            {
                if (handles[i].DistanceTo(_pressWorld) <= HandleSize / Zoom)
                {
                    _handle = i;
                    _originalBounds = single.Bounds;
                    Start(Drag.Resize, e);
                    return;
                }
            }
        }
        if (host.Selection.Count == 1 && diagram.Edge(host.Selection[0]) is { } selectedEdge && TryStartWaypoint(host, selectedEdge, e))
            return;

        // Shapes, then lines, then containers: a line inside a package is picked, not the package.
        var (node, hitEdge) = HitAt(host, _pressWorld);
        if (node is not null)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && host.DefaultEdgeType is not null)
            {
                _connectFrom = node.Id;
                _connectType = host.DefaultEdgeType;
                Start(Drag.Connect, e);
                return;
            }
            if (ctrl)
            {
                if (host.Selection.Contains(node.Id))
                    host.Select(host.Selection.Where(id => id != node.Id).ToList());
                else
                    host.Select([node.Id], add: true);
            }
            else if (!host.Selection.Contains(node.Id))
                host.Select([node.Id]);
            _moving = TopLevel(diagram, host.Selection.Where(id => diagram.Node(id) is not null)).ToList();
            _applied = default;
            if (_moving.Count > 0)
                Start(Drag.Move, e);
            return;
        }

        if (hitEdge is { } edge)
        {
            if (ctrl)
                host.Select([edge.Id], add: true);
            else
                host.Select([edge.Id]);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (!ctrl)
            host.Select([]);
        Start(Drag.Rubber, e);
    }

    /// <summary>What a press at a point lands on, as drawn: a shape, a line, then the innermost container.</summary>
    private (DiagramNode? Node, DiagramEdge? Edge) HitAt(IDiagramCanvasHost host, DPoint at) =>
        Layout.HitAt(host.Diagram, at, ShapeOf, host.Kind.Model.IsContainer, PathOf, 6 / Zoom);

    private bool TryStartWaypoint(IDiagramCanvasHost host, DiagramEdge edge, PointerPressedEventArgs e)
    {
        var path = PathOf(edge);
        _originalWaypoints = [.. edge.Waypoints];
        for (var i = 0; i < edge.Waypoints.Count; i++)
        {
            if (edge.Waypoints[i].DistanceTo(_pressWorld) <= HandleSize / Zoom)
            {
                _waypointEdge = edge.Id;
                _waypointIndex = i;
                Start(Drag.Waypoint, e);
                return true;
            }
        }
        // A segment's middle handle adds a bend there (orthogonal routes become hand-placed bends).
        for (var i = 1; i < path.Count; i++)
        {
            var mid = path[i - 1] + (path[i] - path[i - 1]) * 0.5;
            if (mid.DistanceTo(_pressWorld) > 6 / Zoom)
                continue;
            var waypoints = edge.Waypoints.Count == 0 && path.Count > 2 ? path.Skip(1).Take(path.Count - 2).ToList() : [.. edge.Waypoints];
            var insertAt = Math.Clamp(i - 1, 0, waypoints.Count);
            waypoints.Insert(insertAt, mid);
            edge.Waypoints = waypoints;
            edge.Routing = EdgeRouting.Straight;
            _waypointEdge = edge.Id;
            _waypointIndex = insertAt;
            Start(Drag.Waypoint, e);
            return true;
        }
        return false;
    }

    private void Start(Drag drag, PointerPressedEventArgs e)
    {
        _drag = drag;
        e.Pointer.Capture(this);
        e.Handled = true;
        Cursor = drag == Drag.Pan ? new Cursor(StandardCursorType.SizeAll) : Cursor;
        InvalidateVisual();
    }

    /// <summary>The ids whose containers aren't also selected (moving a container moves what's inside).</summary>
    private static IEnumerable<string> TopLevel(Diagram diagram, IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        foreach (var id in set)
        {
            var inside = false;
            for (var p = diagram.Node(diagram.Node(id)?.ParentId); p is not null; p = diagram.Node(p.ParentId))
                inside |= set.Contains(p.Id);
            if (!inside)
                yield return id;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Host is not { } host)
            return;
        var position = e.GetPosition(this);
        var world = ToWorld(position);
        var diagram = host.Diagram;
        _pointerWorld = world;

        var hover = Layout.NodeAt(diagram, world, ShapeOf)?.Id;
        if (hover != _hover)
        {
            _hover = hover;
            if (host.Tool == CanvasTool.Edge || _drag == Drag.Connect)
                InvalidateVisual();
        }

        switch (_drag)
        {
            case Drag.Pan:
                host.OffsetX -= (position.X - _pressScreen.X) / Zoom;
                host.OffsetY -= (position.Y - _pressScreen.Y) / Zoom;
                _pressScreen = position;
                break;
            case Drag.Move:
                MoveTo(host, world, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
                break;
            case Drag.Resize:
                ResizeTo(host, world, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                break;
            case Drag.Waypoint when diagram.Edge(_waypointEdge) is { } edge && _waypointIndex < edge.Waypoints.Count:
                var points = edge.Waypoints.ToList();
                points[_waypointIndex] = Snap(world);
                edge.Waypoints = points;
                break;
            case Drag.Pen:
                if (_stroke[^1].DistanceTo(world) > 2 / Zoom)
                    _stroke.Add(world);
                break;
            case Drag.Rubber:
            case Drag.Connect:
                break;
            default:
                return;
        }
        InvalidateVisual();
    }

    private void MoveTo(IDiagramCanvasHost host, DPoint world, bool noSnap)
    {
        var diagram = host.Diagram;
        var want = world - _pressWorld;
        var nodes = _moving.Select(diagram.Node).OfType<DiagramNode>().ToList();
        if (nodes.Count == 0)
            return;
        // Where the moved group would be without snapping, then snap its top-left (grid) or edges (guides).
        var group = DRect.Around(nodes.Select(n => n.Bounds)).Offset(want.X - _applied.X, want.Y - _applied.Y);
        _guideX = _guideY = null;
        if (!noSnap)
        {
            var moving = _moving.SelectMany(id => diagram.WithDescendants(id)).Select(n => n.Id).ToHashSet();
            var others = diagram.Nodes.Where(n => !moving.Contains(n.Id) && !host.Kind.Model.IsContainer(n)).Select(n => n.Bounds);
            var (gx, gy, guideX, guideY) = Layout.Guides(group, others, 5 / Zoom);
            if (guideX is not null || guideY is not null)
            {
                want = want + new DPoint(gx, gy);
                (_guideX, _guideY) = (guideX, guideY);
            }
            if (host.SnapToGrid && guideX is null)
                want = want with { X = want.X + (Layout.Snap(group.X, host.GridSize) - group.X) };
            if (host.SnapToGrid && guideY is null)
                want = want with { Y = want.Y + (Layout.Snap(group.Y, host.GridSize) - group.Y) };
        }
        var step = want - _applied;
        if (step.X != 0 || step.Y != 0)
        {
            new MoveNodes(_moving, step.X, step.Y).Apply(diagram);   // live; one operation on release
            _applied = want;
        }
    }

    private void ResizeTo(IDiagramCanvasHost host, DPoint world, bool keepRatio)
    {
        if (SingleNode(host) is not { } node)
            return;
        var p = Snap(world);
        var r = _originalBounds;
        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        if (_handle is 0 or 6 or 7) left = Math.Min(p.X, right - 10);
        if (_handle is 2 or 3 or 4) right = Math.Max(p.X, left + 10);
        if (_handle is 0 or 1 or 2) top = Math.Min(p.Y, bottom - 10);
        if (_handle is 4 or 5 or 6) bottom = Math.Max(p.Y, top + 10);
        if (keepRatio && r.Height > 0)
        {
            var ratio = r.Width / r.Height;
            var height = Math.Max(10, (right - left) / ratio);
            if (_handle is 0 or 1 or 2) top = bottom - height;
            else bottom = top + height;
        }
        (node.X, node.Y, node.Width, node.Height) = (left, top, right - left, bottom - top);   // live; one operation on release
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Host is not { } host)
            return;
        var diagram = host.Diagram;
        var drag = _drag;
        _drag = Drag.None;
        _guideX = _guideY = null;
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;

        switch (drag)
        {
            case Drag.Move when _applied.X != 0 || _applied.Y != 0:
            {
                new MoveNodes(_moving, -_applied.X, -_applied.Y).Apply(diagram);
                var steps = new List<IDiagramOperation> { new MoveNodes(_moving, _applied.X, _applied.Y) };
                // Dropped into (or out of) a container: it becomes the new parent.
                var movingAll = _moving.SelectMany(id => diagram.WithDescendants(id)).Select(n => n.Id).ToHashSet();
                foreach (var id in _moving)
                {
                    if (diagram.Node(id) is not { } n)
                        continue;
                    var landed = n.Bounds.Offset(_applied.X, _applied.Y);
                    var container = Layout.ContainerFor(diagram, landed, host.Kind.Model.IsContainer, movingAll);
                    if (container?.Id != n.ParentId)
                        steps.Add(new SetParent([id], container?.Id));
                }
                host.Do(new Batch(steps[0].Name, steps));
                break;
            }
            case Drag.Resize when SingleNode(host) is { } node && node.Bounds != _originalBounds:
            {
                var now = node.Bounds;
                (node.X, node.Y, node.Width, node.Height) = (_originalBounds.X, _originalBounds.Y, _originalBounds.Width, _originalBounds.Height);
                host.Do(new SetBounds(node.Id, now));
                break;
            }
            case Drag.Rubber:
            {
                var r = DRect.FromCorners(_pressWorld, _pointerWorld);
                if (r.Width > 2 || r.Height > 2)
                {
                    var nodes = diagram.Nodes.Where(n => r.Contains(n.Bounds)).Select(n => n.Id).ToHashSet();
                    var edges = diagram.Edges.Where(ed => nodes.Contains(ed.From) && nodes.Contains(ed.To)).Select(ed => ed.Id);
                    host.Select(nodes.Concat(edges).ToList(), add: e.KeyModifiers.HasFlag(KeyModifiers.Control));
                }
                break;
            }
            case Drag.Connect when _connectFrom is { } from && _connectType is { } type:
            {
                var target = Layout.NodeAt(diagram, _pointerWorld, ShapeOf);
                if (target is not null && (target.Id != from || _pointerWorld.DistanceTo(_pressWorld) > 30))
                    host.Connect(type, from, target.Id);
                _connectFrom = null;
                break;
            }
            case Drag.Waypoint when diagram.Edge(_waypointEdge) is { } edge:
            {
                var now = edge.Waypoints.ToList();
                // A bend dragged back onto the straight line between its neighbours goes away.
                var path = PathOf(edge);
                if (_waypointIndex < now.Count && path.Count >= 3)
                {
                    var i = _waypointIndex + 1;
                    if (i < path.Count - 1 && Shapes.DistanceToSegment(path[i], path[i - 1], path[i + 1]) < 4)
                        now.RemoveAt(_waypointIndex);
                }
                edge.Waypoints = _originalWaypoints;
                var routing = now.Count > 0 ? EdgeRouting.Straight : edge.Routing;
                host.Do(new SetEdgePath(edge.Id, now, routing, edge.LabelAt));
                _waypointEdge = null;
                break;
            }
            case Drag.Pen when _stroke.Count > 1:
                host.AddStroke([.. _stroke]);
                _stroke.Clear();
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Host is not { } host)
            return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var at = e.GetPosition(this);
            var world = ToWorld(at);
            var zoom = Math.Clamp(host.Zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 0.1, 4);
            host.Zoom = zoom;
            host.OffsetX = world.X - at.X / zoom;
            host.OffsetY = world.Y - at.Y / zoom;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            host.OffsetX -= e.Delta.Y * 50 / Zoom;
        else
        {
            host.OffsetX -= e.Delta.X * 50 / Zoom;
            host.OffsetY -= e.Delta.Y * 50 / Zoom;
        }
        PlaceEditor();
        InvalidateVisual();
        e.Handled = true;
    }

    // ---- Keyboard ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Host is not { } host || e.Handled || _editing is not null)
            return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Space:
                _space = true;
                Cursor = new Cursor(StandardCursorType.Hand);
                e.Handled = true;
                return;
            case Key.Escape:
                if (_drag != Drag.None)
                {
                    _drag = Drag.None;
                    _stroke.Clear();
                }
                else if (host.Tool != CanvasTool.Select)
                    host.Tool = CanvasTool.Select;
                else
                    host.Select([]);
                break;
            case Key.F2 or Key.Enter when host.Selection.Count == 1:
                StartEdit(host.Selection[0]);
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when host.Selection.Count > 0:
            {
                var ids = TopLevel(host.Diagram, host.Selection.Where(id => host.Diagram.Node(id) is not null)).ToList();
                if (ids.Count == 0)
                    return;
                var (dx, dy) = e.Key switch { Key.Left => (-step, 0), Key.Right => (step, 0), Key.Up => (0, -step), _ => (0, step) };
                host.Do(new MoveNodes(ids, dx, dy));
                break;
            }
            default:
                return;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
        {
            _space = false;
            Cursor = Cursor.Default;
        }
    }

    // ---- Files dropped from Explorer (pictures on a mood board) ----

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (Host is not { } host || e.DataTransfer.TryGetFiles() is not { Length: > 0 } files)
            return;
        e.Handled = true;
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
            await host.DropFilesAsync(paths, ToWorld(e.GetPosition(this)));
    }
}
