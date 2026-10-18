using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Controls;
using DevHub.Diagrams;
using DevHub.Diagrams.Controls;
using DevHub.Diagrams.Core;
using DevHub.Diagrams.Rendering;
using DevHub.Integration;
using DevHub.Sdk.Core.Localization;
using DevHub.Services;
using DevHub.ViewModels.Dialogs;
using DevHub.ViewModels.Documents;

namespace DevHub.ViewModels.Diagrams;

/// <summary>
/// A diagram or mood board tab (<c>Diagram:&lt;path&gt;</c>): the canvas, the palette (left), the properties of the
/// selection (right), the problems the kind's rules find (bottom), and the toolbar (tools, undo, zoom, arrange,
/// export, the kind's commands). Every change is an operation on the <see cref="UndoStack"/>; Save writes the file.
/// </summary>
public sealed partial class DiagramDocumentViewModel : DocumentViewModelBase, IDiagramCanvasHost, IDisposable
{
    private const string ClipboardPrefix = "devhub-diagram-clip:";

    private readonly DiagramsService _service;
    private readonly IThemeService _theme;
    private readonly Dictionary<string, IssueLinkInfo?> _issues = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _issuesAsked = new(StringComparer.OrdinalIgnoreCase);
    private UndoStack _stack;
    private List<string> _selection = [];
    private DateTime _writtenAt;
    private bool _loaded;
    private CancellationTokenSource? _checkSoon;

    public DiagramDocumentViewModel(string path, DiagramsService service, IDialogService dialogs, IExportService export,
        IStatusService status, IThemeService theme, IIssueLinks? issues = null)
    {
        FilePath = Path.GetFullPath(path);
        _service = service;
        Dialogs = dialogs;
        Export = export;
        Status = status;
        _theme = theme;
        Issues = issues;
        _stack = new UndoStack(new Diagram { Kind = "" });
        Kind = service.Kinds.FirstOrDefault() ?? throw new InvalidOperationException("No diagram kinds.");
        _theme.Changed += OnThemeChanged;
        if (Issues is not null)
            Issues.Changed += OnIssuesChanged;
    }

    public void Dispose()
    {
        _theme.Changed -= OnThemeChanged;
        if (Issues is not null)
            Issues.Changed -= OnIssuesChanged;
        _stack.Changed -= OnStackChanged;
        _checkSoon?.Cancel();
    }

    // ---- Document ----

    public override string Id => DiagramsService.IdFor(FilePath);
    public override string Title => DiagramFile.NameOf(FilePath);
    public override string Icon => Kind.IconKind;
    public override bool FillsEditorArea => true;
    public override string? ContextPath => ProjectFolder;
    public override bool HasUnsavedChanges => _stack.IsDirty;

    public string FilePath { get; private set; }

    public Diagram Diagram => _stack.Diagram;

    [ObservableProperty] private IDiagramKind _kind;

    public IDialogService Dialogs { get; }
    public IExportService Export { get; }
    public IStatusService Status { get; }
    public IIssueLinks? Issues { get; }

    public string? ProjectFolder => ProjectLinks.Resolve(FilePath, Diagram);

    /// <summary>"Linked to shop-web" / "Not linked to a project".</summary>
    public string ProjectText => ProjectFolder is { } folder
        ? (Directory.Exists(folder) ? L.T($"Project: {Path.GetFileName(folder)}") : L.T($"Project not found: {Diagram.Project}"))
        : L.T("Not linked to a project");

    [ObservableProperty] private string? _loadError;
    [ObservableProperty] private bool _isReadOnly;

    protected override async void OnActivationChanged(bool isActive)
    {
        if (!isActive)
            return;
        if (!_loaded)
        {
            _loaded = true;
            await LoadAsync();
        }
        else if (!_stack.IsDirty && File.Exists(FilePath) && File.GetLastWriteTimeUtc(FilePath) != _writtenAt)
            await LoadAsync();   // changed on disk (a git pull, another editor) and nothing to lose here
    }

    private async Task LoadAsync()
    {
        try
        {
            var diagram = await DiagramFile.LoadAsync(FilePath);
            _writtenAt = File.GetLastWriteTimeUtc(FilePath);
            if (_service.Kind(diagram.Kind) is not { } kind)
            {
                LoadError = L.T($"This is a \"{diagram.Kind}\" diagram, and the plugin that draws them is off (Settings → Plugins).");
                return;
            }
            IsReadOnly = diagram.SchemaVersion > Diagram.CurrentSchema;
            _stack.Changed -= OnStackChanged;
            _stack = new UndoStack(diagram);
            _stack.Changed += OnStackChanged;
            Kind = kind;
            LoadError = null;
            OnPropertyChanged(nameof(Diagram));
            OnPropertyChanged(nameof(Icon));
            BuildPalette();
            BuildCommands();
            _selection = [];
            SelectionChanged();
            Check();
            Redraw?.Invoke(this, true);
            OnPropertyChanged(nameof(ProjectText));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DiagramFileException)
        {
            LoadError = L.T($"The diagram couldn't be read: {e.Message}");
        }
    }

    private void OnStackChanged(object? sender, EventArgs e)
    {
        IsDirty = _stack.IsDirty;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
        _selection = _selection.Where(Diagram.Has).ToList();
        SelectionChanged();
        foreach (var f in Fields)
            f.Refresh();
        Redraw?.Invoke(this, false);
        CheckSoon();
    }

    public string UndoText => _stack.UndoName is { } name ? L.T($"Undo: {name}") : L.T("Undo");
    public string RedoText => _stack.RedoName is { } name ? L.T($"Redo: {name}") : L.T("Redo");

    public bool Do(IDiagramOperation operation)
    {
        if (IsReadOnly)
        {
            Status.Show(L.T("This diagram was made by a newer DevHub: it opens read-only."));
            return false;
        }
        try
        {
            _stack.Do(operation);
            return true;
        }
        catch (DiagramRuleException e)
        {
            Status.Show(e.Message);
            Redraw?.Invoke(this, false);
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => _stack.Undo();

    private bool CanUndo() => _stack.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => _stack.Redo();

    private bool CanRedo() => _stack.CanRedo;

    [RelayCommand]
    public async Task SaveAsync()
    {
        try
        {
            await DiagramFile.SaveAsync(Diagram, FilePath);
            _writtenAt = File.GetLastWriteTimeUtc(FilePath);
            _stack.MarkSaved();
            _service.RaiseChanged();
            Status.Show(L.T($"Saved {Path.GetFileName(FilePath)}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status.Show(L.T($"The diagram couldn't be saved: {e.Message}"));
        }
    }

    public override async Task<bool> ConfirmCloseAsync()
    {
        if (!_stack.IsDirty)
            return true;
        var save = new ConfirmDialogViewModel(L.T($"Save {Title}?"),
            L.T("The diagram has changes that aren't saved yet. Save them before closing?"), L.T("Save"), null, destructive: false);
        if (await Dialogs.ShowDialogAsync(save))
        {
            await SaveAsync();
            return !_stack.IsDirty;
        }
        var discard = new ConfirmDialogViewModel(L.T("Discard changes?"), L.T("The changes since the last save are lost."), L.T("Discard"), null);
        return await Dialogs.ShowDialogAsync(discard);
    }

    // ---- View state (the canvas) ----

    [ObservableProperty] private double _zoom = 1;
    [ObservableProperty] private double _offsetX;
    [ObservableProperty] private double _offsetY;
    [ObservableProperty] private bool _showGrid = true;
    [ObservableProperty] private bool _snapToGrid = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinimapShown))]
    private bool _showMinimap = true;
    [ObservableProperty] private string _penColor = PenColors.All[0];
    [ObservableProperty] private double _penWidth = 3;
    public IReadOnlyList<string> PenColorChoices => PenColors.All;

    [RelayCommand]
    private void SetPenColor(string color)
    {
        PenColor = color;
        Tool = CanvasTool.Pen;
    }
    public double GridSize => 10;
    public double ViewportWidth { get; private set; } = 800;
    public double ViewportHeight { get; private set; } = 600;

    public string ZoomText => ZoomLevels.Text(Zoom);

    partial void OnZoomChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomText));
        Redraw?.Invoke(this, false);
    }

    partial void OnOffsetXChanged(double value) => Redraw?.Invoke(this, false);
    partial void OnOffsetYChanged(double value) => Redraw?.Invoke(this, false);
    partial void OnShowGridChanged(bool value) => Redraw?.Invoke(this, false);

    public void ViewportChanged(double width, double height)
    {
        (ViewportWidth, ViewportHeight) = (width, height);
        OnPropertyChanged(nameof(MinimapShown));
    }

    /// <summary>The overview, when it's on and the canvas is big enough that it doesn't hide the diagram.</summary>
    public bool MinimapShown => ShowMinimap && ViewportWidth >= 640 && ViewportHeight >= 400;

    public DPoint ViewCenter => new(OffsetX + ViewportWidth / Zoom / 2, OffsetY + ViewportHeight / Zoom / 2);

    public DiagramStyle Style
    {
        get
        {
            var style = _theme.IsDark ? DiagramStyle.Dark : DiagramStyle.Light;
            return Diagram.Props.GetValueOrDefault("courseColours") == "true" && !_theme.IsDark ? style.WithCourseColours() : style;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Redraw?.Invoke(this, false);

    public event EventHandler<bool>? Redraw;
    public event EventHandler<string>? EditTextRequested;

    [RelayCommand]
    private void ZoomIn() => ZoomAround(Zoom * 1.25);

    [RelayCommand]
    private void ZoomOut() => ZoomAround(Zoom / 1.25);

    [RelayCommand]
    private void ZoomReset() => ZoomAround(1);

    [RelayCommand]
    private void ZoomFit() => Redraw?.Invoke(this, true);

    private void ZoomAround(double zoom)
    {
        var center = ViewCenter;
        Zoom = Math.Clamp(zoom, 0.1, 4);
        OffsetX = center.X - ViewportWidth / Zoom / 2;
        OffsetY = center.Y - ViewportHeight / Zoom / 2;
    }

    // ---- Tools and palette ----

    [ObservableProperty] private CanvasTool _tool = CanvasTool.Select;
    [ObservableProperty] private string? _toolType;
    public string? DefaultEdgeType => _lastEdgeType ?? Kind.Model.EdgeTypes.FirstOrDefault()?.Type;
    private string? _lastEdgeType;

    public ObservableCollection<PaletteItemViewModel> Shapes { get; } = [];
    public ObservableCollection<PaletteItemViewModel> Lines { get; } = [];
    public bool HasLines => Lines.Count > 0;
    public bool HasPen => Kind.HasPen;

    public bool IsSelectTool { get => Tool == CanvasTool.Select; set { if (value) UseTool(CanvasTool.Select); } }
    public bool IsPanTool { get => Tool == CanvasTool.Pan; set { if (value) UseTool(CanvasTool.Pan); } }
    public bool IsPenTool { get => Tool == CanvasTool.Pen; set { if (value) UseTool(CanvasTool.Pen); } }
    public bool IsEraserTool { get => Tool == CanvasTool.Eraser; set { if (value) UseTool(CanvasTool.Eraser); } }

    private void UseTool(CanvasTool tool)
    {
        ToolType = null;
        Tool = tool;
    }

    partial void OnToolChanged(CanvasTool value)
    {
        OnPropertyChanged(nameof(IsSelectTool));
        OnPropertyChanged(nameof(IsPanTool));
        OnPropertyChanged(nameof(IsPenTool));
        OnPropertyChanged(nameof(IsEraserTool));
        foreach (var item in Shapes.Concat(Lines))
            item.IsActive = (value == CanvasTool.Node && !item.IsEdge || value == CanvasTool.Edge && item.IsEdge) && item.Type == ToolType;
        Redraw?.Invoke(this, false);
    }

    private void BuildPalette()
    {
        Shapes.Clear();
        Lines.Clear();
        foreach (var t in Kind.Model.NodeTypes.Where(t => !t.Hidden))
            Shapes.Add(new PaletteItemViewModel(t.Name, t.Description, t.Type, false, Pick));
        foreach (var t in Kind.Model.EdgeTypes)
            Lines.Add(new PaletteItemViewModel(t.Name, t.Description, t.Type, true, Pick));
        OnPropertyChanged(nameof(HasLines));
        OnPropertyChanged(nameof(HasPen));
    }

    private void Pick(PaletteItemViewModel item)
    {
        ToolType = item.Type;
        if (item.IsEdge)
            _lastEdgeType = item.Type;
        Tool = item.IsEdge ? CanvasTool.Edge : CanvasTool.Node;
        OnToolChanged(Tool);
    }

    public void CreateNode(string type, DPoint center)
    {
        var node = Kind.Model.NewNode(Diagram, type, center);
        var steps = new List<IDiagramOperation> { new AddElements([node], [], L.T($"Add {Kind.Model.NodeType(type)?.Name}")) };
        if (Layout.ContainerFor(Diagram, node.Bounds, Kind.Model.IsContainer, []) is { } container && !Kind.Model.IsContainer(node))
            steps.Add(new SetParent([node.Id], container.Id));
        if (Do(new Batch(steps[0].Name, steps)))
        {
            Select([node.Id]);
            if (node.Text.Length > 0)
                EditTextRequested?.Invoke(this, node.Id);
        }
    }

    public void Connect(string edgeType, string fromId, string toId)
    {
        if (Diagram.Node(fromId) is not { } from || Diagram.Node(toId) is not { } to)
            return;
        var edge = Kind.Model.NewEdge(Diagram, edgeType, from, to);
        if (edge is null)
        {
            var name = Kind.Model.EdgeType(edgeType)?.Name ?? edgeType;
            Status.Show(L.T($"A {name} can't join a {Kind.Model.NodeType(from.Type)?.Name} and a {Kind.Model.NodeType(to.Type)?.Name}."));
            return;
        }
        if (Do(new AddElements([], [edge], L.T("Connect"))))
            Select([edge.Id]);
    }

    public void AddStroke(IReadOnlyList<DPoint> points)
    {
        if (Kind.Stroke(Diagram, points, PenColor, PenWidth) is { } op)
            Do(op);
    }

    public async Task DropFilesAsync(IReadOnlyList<string> paths, DPoint at)
    {
        if (!await Kind.DropFilesAsync(this, paths, at))
            Status.Show(L.T("This kind of diagram doesn't take files: drop pictures on a mood board."));
    }

    // ---- Selection and properties ----

    public IReadOnlyList<string> Selection => _selection;

    public void Select(IEnumerable<string> ids, bool add = false)
    {
        var list = ids.Where(Diagram.Has).ToList();
        _selection = add ? _selection.Concat(list).Distinct().ToList() : list;
        SelectionChanged();
        Redraw?.Invoke(this, false);
    }

    [ObservableProperty] private string _selectionTitle = "";
    public ObservableCollection<PropertyFieldViewModel> Fields { get; } = [];
    public bool HasSelection => _selection.Count > 0;
    public bool HasSingleNode => _selection.Count == 1 && Diagram.Node(_selection[0]) is not null;
    public bool HasSeveral => _selection.Count > 1;

    private void SelectionChanged()
    {
        Fields.Clear();
        if (_selection.Count == 1 && Diagram.Node(_selection[0]) is { } node)
        {
            SelectionTitle = Kind.Model.NodeType(node.Type)?.Name ?? node.Type;
            foreach (var f in Kind.NodeFields(node))
                Fields.Add(Field(node.Id, f));
        }
        else if (_selection.Count == 1 && Diagram.Edge(_selection[0]) is { } edge)
        {
            SelectionTitle = Kind.Model.EdgeType(edge.Type)?.Name ?? edge.Type;
            foreach (var f in Kind.EdgeFields(edge))
                Fields.Add(Field(edge.Id, f));
        }
        else if (_selection.Count > 1)
            SelectionTitle = L.T($"{_selection.Count} selected");
        else
        {
            SelectionTitle = Kind.Model.Name;
            foreach (var f in Kind.DiagramFields)
                Fields.Add(Field(null, f));
        }
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasSingleNode));
        OnPropertyChanged(nameof(HasSeveral));
        OnPropertyChanged(nameof(LinkedIssue));
        OnPropertyChanged(nameof(HasLinkedIssue));
        DeleteCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        LinkIssueCommand.NotifyCanExecuteChanged();
        CreateStoryCommand.NotifyCanExecuteChanged();
        OpenIssueCommand.NotifyCanExecuteChanged();
        UnlinkIssueCommand.NotifyCanExecuteChanged();
    }

    private PropertyFieldViewModel Field(string? id, PropertyField f) => new(f, () => Read(id, f), value => Write(id, f, value));

    private string Read(string? id, PropertyField f)
    {
        if (id is null)
            return f.Key switch
            {
                "@title" => Diagram.Title,
                _ => Diagram.Props.GetValueOrDefault(f.Key, f.Default),
            };
        if (Diagram.Node(id) is { } n)
            return f.Key switch
            {
                "@text" => n.Text,
                "@width" => Math.Round(n.Width).ToString(System.Globalization.CultureInfo.CurrentCulture),
                "@height" => Math.Round(n.Height).ToString(System.Globalization.CultureInfo.CurrentCulture),
                "@rotation" => Math.Round(n.Rotation).ToString(System.Globalization.CultureInfo.CurrentCulture),
                _ => n.Props.GetValueOrDefault(f.Key, f.Default),
            };
        if (Diagram.Edge(id) is { } e)
            return f.Key switch
            {
                "@text" => e.Text,
                "@routing" => e.Routing == EdgeRouting.Orthogonal ? "orthogonal" : "straight",
                _ => e.Props.GetValueOrDefault(f.Key, f.Default),
            };
        return "";
    }

    private void Write(string? id, PropertyField f, string value)
    {
        IDiagramOperation op = (id, f.Key) switch
        {
            (null, "@title") => new SetDiagramInfo(value, Diagram.Project),
            (null, _) => new SetProps(null, new Dictionary<string, string?> { [f.Key] = value }, f.Label),
            (_, "@text") => new SetText(id, value),
            (_, "@width" or "@height") when Diagram.Node(id) is { } n && double.TryParse(value, out var size) && size > 0 =>
                new SetBounds(id, f.Key == "@width" ? n.Bounds with { Width = size } : n.Bounds with { Height = size }),
            (_, "@rotation") when double.TryParse(value, out var degrees) => new SetRotation(id, degrees),
            (_, "@routing") when Diagram.Edge(id) is { } e =>
                new SetEdgePath(id, value == "orthogonal" ? [] : e.Waypoints, value == "orthogonal" ? EdgeRouting.Orthogonal : EdgeRouting.Straight, e.LabelAt),
            (_, "@width" or "@height" or "@rotation" or "@routing") => new Batch(f.Label, []),
            _ => new SetProps(id, new Dictionary<string, string?> { [f.Key] = value }, f.Label),
        };
        if (Do(op) && id is not null && Diagram.Node(id) is { } node && Kind.Fit(node, MeasureSurface.Instance) is { } fit && fit != node.Bounds)
            Do(new SetBounds(id, fit));
        if (id is null && f.Key == "courseColours")
            Redraw?.Invoke(this, false);
    }

    [RelayCommand]
    private void SelectAll() => Select(Diagram.Nodes.Select(n => n.Id).Concat(Diagram.Edges.Select(e => e.Id)).ToList());

    [RelayCommand]
    private void EditText()
    {
        if (_selection.Count == 1)
            EditTextRequested?.Invoke(this, _selection[0]);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete() => Do(new RemoveElements(_selection.ToList()));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyAsync()
    {
        var (nodes, edges) = SelectedElements();
        var json = JsonSerializer.Serialize(new Clip(nodes, edges));
        await Export.CopyTextAsync(ClipboardPrefix + Kind.Model.Id + ":" + json);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CutAsync()
    {
        await CopyAsync();
        Delete();
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        var text = await Export.GetClipboardTextAsync();
        if (text is not null && text.StartsWith(ClipboardPrefix, StringComparison.Ordinal))
        {
            var rest = text[ClipboardPrefix.Length..];
            var colon = rest.IndexOf(':');
            if (colon > 0 && rest[..colon] == Kind.Model.Id && JsonSerializer.Deserialize<Clip>(rest[(colon + 1)..]) is { } clip)
            {
                PasteClip(clip, 20);
                return;
            }
            Status.Show(L.T("Shapes from another kind of diagram can't be pasted here."));
            return;
        }
        if (!await Kind.PasteAsync(this, ViewCenter))
            Status.Show(L.T("Nothing to paste here."));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Duplicate()
    {
        var (nodes, edges) = SelectedElements();
        PasteClip(new Clip(nodes, edges), 20);
    }

    private sealed record Clip(List<DiagramNode> Nodes, List<DiagramEdge> Edges);

    private (List<DiagramNode> Nodes, List<DiagramEdge> Edges) SelectedElements()
    {
        var ids = _selection.SelectMany(id => Diagram.Node(id) is null ? [] : Diagram.WithDescendants(id)).Select(n => n.Id).ToHashSet();
        var nodes = Diagram.Nodes.Where(n => ids.Contains(n.Id)).ToList();
        var edges = Diagram.Edges.Where(e => ids.Contains(e.From) && ids.Contains(e.To)).ToList();
        return (nodes, edges);
    }

    private void PasteClip(Clip clip, double offset)
    {
        var copy = new Diagram { Kind = Diagram.Kind, Nodes = clip.Nodes, Edges = clip.Edges }.Clone();
        var map = new Dictionary<string, string>();
        foreach (var n in copy.Nodes)
            map[n.Id] = DiagramIds.NewIn(Diagram);
        foreach (var n in copy.Nodes)
        {
            n.Id = map[n.Id];
            n.ParentId = n.ParentId is { } p && map.TryGetValue(p, out var np) ? np : null;
            n.X += offset;
            n.Y += offset;
        }
        foreach (var e in copy.Edges)
        {
            e.Id = DiagramIds.NewIn(Diagram);
            e.From = map[e.From];
            e.To = map[e.To];
            e.Waypoints = e.Waypoints.Select(w => new DPoint(w.X + offset, w.Y + offset)).ToList();
        }
        if (Do(new AddElements(copy.Nodes, copy.Edges, L.T("Paste"))))
            Select(copy.Nodes.Where(n => n.ParentId is null || !map.ContainsValue(n.ParentId)).Select(n => n.Id).ToList());
    }

    [RelayCommand]
    private void BringToFront() => Do(new Reorder(_selection.Where(id => Diagram.Node(id) is not null).ToList(), true));

    [RelayCommand]
    private void SendToBack() => Do(new Reorder(_selection.Where(id => Diagram.Node(id) is not null).ToList(), false));

    [RelayCommand]
    private void Align(string how) => Do(Layout.Align(Diagram, _selection, Enum.Parse<Alignment>(how)));

    [RelayCommand]
    private void Distribute(string direction) => Do(Layout.Distribute(Diagram, _selection, direction == "Horizontal"));

    /// <summary>Arrange: layered layout of the selection (or everything), in the kind's direction.</summary>
    [RelayCommand]
    private void Arrange()
    {
        var ids = _selection.Count > 1
            ? _selection.Where(id => Diagram.Node(id) is { } n && !Kind.Model.IsContainer(n)).ToList()
            : Diagram.Nodes.Where(n => !Kind.Model.IsContainer(n) && Kind.Model.ShapeOf(n) != NodeShape.Path).Select(n => n.Id).ToList();
        var upwards = new HashSet<string>(StringComparer.Ordinal) { "generalization", "realization" };
        Do(AutoLayout.Layered(Diagram, ids, leftToRight: Diagram.Kind == "usecase", reversed: upwards));
        Redraw?.Invoke(this, true);
    }

    // ---- Problems ----

    public ObservableCollection<ProblemViewModel> Problems { get; } = [];
    public bool HasProblems => Problems.Count > 0;
    public string ProblemsTitle => Problems.Count == 0 ? L.T("No problems") : L.T($"Problems ({Problems.Count})");

    private void CheckSoon()
    {
        _checkSoon?.Cancel();
        _checkSoon = new CancellationTokenSource();
        var token = _checkSoon.Token;
        _ = Task.Delay(400, token).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(Check), token,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void Check()
    {
        Problems.Clear();
        foreach (var p in Kind.Model.Check(Diagram))
            Problems.Add(new ProblemViewModel(p, ShowProblem));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(ProblemsTitle));
    }

    private void ShowProblem(DiagramProblem problem)
    {
        Select(problem.ElementIds);
        var nodes = problem.ElementIds.Select(Diagram.Node).OfType<DiagramNode>().ToList();
        if (nodes.Count > 0)
        {
            var area = DRect.Around(nodes.Select(n => n.Bounds));
            OffsetX = area.Center.X - ViewportWidth / Zoom / 2;
            OffsetY = area.Center.Y - ViewportHeight / Zoom / 2;
        }
    }

    // ---- Kind commands and text exports ----

    public ObservableCollection<EditorCommandViewModel> KindCommands { get; } = [];
    public ObservableCollection<EditorCommandViewModel> TextExports { get; } = [];
    public bool HasKindCommands => KindCommands.Count > 0;
    public bool HasTextExports => TextExports.Count > 0;

    private void BuildCommands()
    {
        KindCommands.Clear();
        foreach (var c in Kind.Commands)
            KindCommands.Add(new EditorCommandViewModel(c.Name, c.Icon, c.Description, async () =>
            {
                if (c.CanRun(this))
                    await c.Run(this);
            }));
        TextExports.Clear();
        foreach (var exporter in Kind.Model.TextExporters)
        {
            TextExports.Add(new EditorCommandViewModel(L.T($"Copy as {exporter.Name}"), IconKind.Copy, "", () => Export.CopyTextAsync(exporter.Export(Diagram))));
            TextExports.Add(new EditorCommandViewModel(L.T($"Save as {exporter.Name}…"), IconKind.Import, "",
                () => Export.SaveTextAsync(exporter.Export(Diagram), Title, exporter.Extension, exporter.Name)));
        }
        OnPropertyChanged(nameof(HasKindCommands));
        OnPropertyChanged(nameof(HasTextExports));
    }

    public async Task OpenGeneratedAsync(Diagram diagram, string path)
    {
        try
        {
            await DiagramFile.SaveAsync(diagram, path);
            _service.RaiseChanged();
            _service.Open(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status.Show(L.T($"The diagram couldn't be saved: {e.Message}"));
        }
    }

    // ---- Export ----

    private IReadOnlyCollection<string>? ExportPart => _selection.Count > 0 ? _selection : null;

    private string? Folder => FilePath is { } file ? Path.GetDirectoryName(file) : null;

    private DiagramStyle ExportStyle => Diagram.Props.GetValueOrDefault("courseColours") == "true"
        ? DiagramStyle.Light.WithCourseColours()
        : DiagramStyle.Light;

    [RelayCommand]
    private Task SaveImageAsync() => Export.SaveImageAsync(DiagramRenderer.Png(Diagram, Kind, ExportStyle, ExportPart, folder: Folder), Title);

    [RelayCommand]
    private Task CopyImageAsync() => Export.CopyImageAsync(DiagramRenderer.Png(Diagram, Kind, ExportStyle, ExportPart, folder: Folder));

    [RelayCommand]
    private Task SaveSvgAsync() => Export.SaveTextAsync(DiagramRenderer.Svg(Diagram, Kind, ExportStyle, ExportPart, Folder), Title, "svg", L.T("SVG image"));

    // ---- Project link ----

    [RelayCommand]
    private async Task LinkToProjectAsync()
    {
        var dialog = new LinkProjectDialogViewModel(_service.Catalog, Dialogs, ProjectFolder);
        if (!await Dialogs.ShowDialogAsync(dialog))
            return;
        var project = dialog.Folder is { } folder ? ProjectLinks.Relative(FilePath, folder) : null;
        if (Do(new SetDiagramInfo(Diagram.Title, project)))
        {
            OnPropertyChanged(nameof(ProjectText));
            OnPropertyChanged(nameof(ContextPath));
            await SaveAsync();
        }
    }

    // ---- Board link (issues) ----

    public bool HasBoard => Issues is not null;

    /// <summary>The issue key of the selected shape, or null.</summary>
    public string? LinkedIssue => HasSingleNode && Diagram.Node(_selection[0])!.Prop("issue") is { Length: > 0 } key ? key : null;

    public bool HasLinkedIssue => LinkedIssue is not null;

    public IssueLinkInfo? Issue(string key)
    {
        if (Issues is null)
            return null;
        if (_issues.TryGetValue(key, out var info))
            return info;
        if (_issuesAsked.Add(key))
            _ = FetchIssueAsync(key);
        return null;
    }

    private async Task FetchIssueAsync(string key)
    {
        var info = await Issues!.FindAsync(key);
        _issues[key] = info;
        Redraw?.Invoke(this, false);
    }

    private void OnIssuesChanged(object? sender, EventArgs e)
    {
        _issues.Clear();
        _issuesAsked.Clear();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Redraw?.Invoke(this, false));
    }

    private bool CanLinkIssue() => Issues is not null && HasSingleNode;

    [RelayCommand(CanExecute = nameof(CanLinkIssue))]
    private async Task LinkIssueAsync()
    {
        if (Issues is null || _selection.Count != 1)
            return;
        var id = _selection[0];
        var dialog = new LinkIssueDialogViewModel(Issues);
        await dialog.SearchAsync();
        if (await Dialogs.ShowDialogAsync(dialog) && dialog.Selected is { } issue)
        {
            _issues[issue.Key] = issue;
            Do(new SetProps(id, new Dictionary<string, string?> { ["issue"] = issue.Key }, L.T("Link to Issue")));
            OnPropertyChanged(nameof(LinkedIssue));
            OnPropertyChanged(nameof(HasLinkedIssue));
        }
    }

    [RelayCommand(CanExecute = nameof(CanLinkIssue))]
    private async Task CreateStoryAsync()
    {
        if (Issues is null || _selection.Count != 1 || Diagram.Node(_selection[0]) is not { } node)
            return;
        var description = L.T($"From the diagram \"{Title}\" ({Kind.Model.NodeType(node.Type)?.Name}).");
        if (await Issues.CreateStoryAsync(node.Text.Replace('\n', ' '), description) is { } issue)
        {
            _issues[issue.Key] = issue;
            Do(new SetProps(node.Id, new Dictionary<string, string?> { ["issue"] = issue.Key }, L.T("Create Story")));
            OnPropertyChanged(nameof(LinkedIssue));
            OnPropertyChanged(nameof(HasLinkedIssue));
            Status.Show(L.T($"{issue.Key} created and linked."));
        }
    }

    private bool HasIssueLink() => Issues is not null && LinkedIssue is not null;

    [RelayCommand(CanExecute = nameof(HasIssueLink))]
    private void OpenIssue()
    {
        if (LinkedIssue is { } key)
            Issues?.Open(key);
    }

    [RelayCommand(CanExecute = nameof(HasIssueLink))]
    private void UnlinkIssue()
    {
        if (_selection.Count == 1)
            Do(new SetProps(_selection[0], new Dictionary<string, string?> { ["issue"] = null }, L.T("Unlink Issue")));
        OnPropertyChanged(nameof(LinkedIssue));
        OnPropertyChanged(nameof(HasLinkedIssue));
    }

    [RelayCommand]
    private void ShowInExplorer()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", FilePath } });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status.Show(e.Message);
        }
    }

    public override string ToString() => Title;
}
