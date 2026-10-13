using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using DevHub.Controls;
using DevHub.Services.Board;
using DevHub.ViewModels.Board;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Documents;

/// <summary>A page of the board tab as the page strip shows it.</summary>
public sealed record BoardPageTab(IBoardPageProvider Provider)
{
    public string Id => Provider.Id;
    public string Title => Provider.Title;
    public override string ToString() => Title;
}

/// <summary>
/// A board tab ("Board:folder|C:\…"): the board selector, the pages (Board, Backlog, Search and those of other
/// plugins), the search box, and the issue panel on the right. Loads when first shown; one tab per board.
/// </summary>
public sealed partial class BoardDocumentViewModel : DocumentViewModelBase, IDisposable
{
    public const string IdPrefix = "Board:";

    private readonly BoardUiServices _services;
    private BoardContext? _context;
    private bool _loading;
    private bool _disposed;

    public BoardDocumentViewModel(BoardLocation location, BoardUiServices services)
    {
        Location = location;
        _services = services;
        _title = Path.GetFileName(location.Location.TrimEnd('\\', '/'));
    }

    public BoardLocation Location { get; }

    public override string Id => IdPrefix + Location.Key;
    public override string Title => _title;
    public override string Icon => IconKind.Menu;
    public override bool FillsEditorArea => true;
    public override string? ContextPath => Location.Location;

    private string _title;

    public BoardContext? Context => _context;

    /// <summary>Loading, the board, or a message (no board here / can't be read).</summary>
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _canCreateHere;

    public ObservableCollection<BoardDefinition> Boards { get; } = [];
    [ObservableProperty] private BoardDefinition? _selectedBoard;

    public ObservableCollection<BoardPageTab> Pages { get; } = [];
    [ObservableProperty] private BoardPageTab? _selectedPage;
    [ObservableProperty] private BoardPageViewModel? _page;

    [ObservableProperty] private string _search = "";

    /// <summary>The issue shown on the right (null = panel closed).</summary>
    [ObservableProperty] private IssueDetailViewModel? _issuePanel;

    public bool HasIssuePanel => IssuePanel is not null;

    /// <summary>The issue panel's width (dragged with its splitter).</summary>
    [ObservableProperty] private double _issuePanelWidth = 440;

    [ObservableProperty] private string? _undoText;

    private BoardPageHost? _host;
    private readonly Dictionary<string, BoardPageViewModel> _pageCache = new(StringComparer.Ordinal);

    protected override void OnActivationChanged(bool isActive)
    {
        if (isActive && _context is null && !_loading)
            _ = LoadAsync();
        Page?.OnShown(isActive);
    }

    private async Task LoadAsync()
    {
        _loading = true;
        Message = null;
        try
        {
            _context = await _services.Boards.AcquireAsync(Location);
            if (_disposed)
            {
                if (_context is not null)
                    _services.Boards.Release(_context);
                return;
            }
            if (_context is null)
            {
                Message = $"There's no board in {Location.Location} yet.";
                CanCreateHere = true;
                return;
            }
            _context.Changed += OnBoardChanged;
            OnPropertyChanged(nameof(Context));
            ShowBoard();
        }
        catch (Exception e) when (e is BoardStoreException or IOException or UnauthorizedAccessException)
        {
            Message = "The board can't be opened: " + e.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void ShowBoard()
    {
        var context = _context!;
        _title = context.Project.Key + " board";
        OnPropertyChanged(nameof(Title));
        IsLoaded = true;
        CanCreateHere = false;
        SyncBoards();
        var last = _services.Boards.LastBoardView(Location);
        SelectedBoard = Boards.FirstOrDefault(b => b.Id == last) ?? Boards.FirstOrDefault();
        UndoText = context.Session.UndoDescription;
    }

    [RelayCommand]
    private async Task CreateBoardHereAsync()
    {
        var dialog = new NewBoardDialogViewModel(_services.Boards, Location, _services.Dialogs);
        if (await _services.Dialogs.ShowDialogAsync(dialog) && dialog.Created is { } created)
        {
            // The dialog created and opened it; this tab takes it over.
            _context = created;
            _context.Changed += OnBoardChanged;
            OnPropertyChanged(nameof(Context));
            Message = null;
            ShowBoard();
        }
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    private void SyncBoards()
    {
        var definitions = _context!.Snapshot.Boards.Values.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var selectedId = SelectedBoard?.Id;
        Boards.Clear();
        foreach (var d in definitions)
            Boards.Add(d);
        // Keep the selection by id (the definition object is new after every change).
        var again = Boards.FirstOrDefault(b => b.Id == selectedId);
        if (again is not null && !ReferenceEquals(again, SelectedBoard))
        {
            _suppressBoardChange = true;
            SelectedBoard = again;
            _suppressBoardChange = false;
            if (_host is not null)
            {
                // Scrum ↔ Kanban changes which pages apply (Board.Scrum's planning page): rebuild them.
                var kindChanged = _host.Definition.Kind != again.Kind;
                _host.Definition = again;
                if (kindChanged)
                    RebuildPages(again);
            }
        }
        else if (again is null && Boards.Count > 0)
        {
            SelectedBoard = Boards[0];
        }
    }

    private bool _suppressBoardChange;

    partial void OnSelectedBoardChanged(BoardDefinition? value)
    {
        if (_suppressBoardChange || value is null || _context is null)
            return;
        _services.Boards.SetLastBoardView(Location, value.Id);
        RebuildPages(value);
    }

    // A different board definition: its own pages (a Scrum board has sprint pages), fresh page ViewModels.
    private void RebuildPages(BoardDefinition definition)
    {
        var keepPage = SelectedPage?.Id ?? "board";
        foreach (var page in _pageCache.Values)
            page.Dispose();
        _pageCache.Clear();
        Page = null;
        _host = new BoardPageHost(_context!, definition)
        {
            SelectIssue = ShowIssue,
            OpenIssue = OpenIssue,
            CreateIssue = CreateIssueWithAsync,
            ShowPage = id => SelectedPage = Pages.FirstOrDefault(p => p.Id == id) ?? SelectedPage,
        };
        Pages.Clear();
        foreach (var provider in _services.PagesFor(definition))
            Pages.Add(new BoardPageTab(provider));
        SelectedPage = Pages.FirstOrDefault(p => p.Id == keepPage) ?? Pages.FirstOrDefault();
    }

    partial void OnSelectedPageChanged(BoardPageTab? value)
    {
        Page?.OnShown(false);
        if (value is null || _host is null)
        {
            Page = null;
            return;
        }
        if (!_pageCache.TryGetValue(value.Id, out var page))
        {
            page = value.Provider.Create(_host);
            _pageCache[value.Id] = page;
        }
        Page = page;
        page.OnSearch(Search);
        page.OnShown(true);
    }

    partial void OnSearchChanged(string value) => Page?.OnSearch(value);

    partial void OnIssuePanelChanged(IssueDetailViewModel? oldValue, IssueDetailViewModel? newValue)
    {
        oldValue?.Dispose();
        OnPropertyChanged(nameof(HasIssuePanel));
    }

    private void OnBoardChanged(object? sender, EventArgs e)
    {
        if (_context is null)
            return;
        SyncBoards();
        UndoText = _context.Session.UndoDescription;
        foreach (var page in _pageCache.Values)
            page.OnBoardChanged();
        if (IssuePanel is { } panel)
        {
            if (_context.Snapshot.Issue(panel.IssueId) is null)
                IssuePanel = null;
            else
                panel.Refresh();
        }
        var key = _context.Project.Key + " board";
        if (key != _title)
        {
            _title = key;
            OnPropertyChanged(nameof(Title));
        }
    }

    // ---- Issues -------------------------------------------------------------------------------------------

    public void ShowIssue(string? issueId)
    {
        if (_context is null)
            return;
        if (issueId is null)
        {
            IssuePanel = null;
            return;
        }
        if (IssuePanel?.IssueId == issueId)
            return;
        IssuePanel = new IssueDetailViewModel(_context, issueId, _services, isPanel: true)
        {
            OpenInTab = OpenIssue,
            Close = () => IssuePanel = null,
            Navigate = ShowIssue,
        };
    }

    public void OpenIssue(string issueId) => OpenDocument?.Invoke(IssueDocumentFactory.IdFor(Location, issueId));

    [RelayCommand]
    private void CloseIssuePanel() => IssuePanel = null;

    [RelayCommand]
    private Task CreateIssueAsync() => CreateIssueWithAsync(new CreateIssueDefaults());

    private async Task CreateIssueWithAsync(CreateIssueDefaults defaults)
    {
        if (_context is null)
            return;
        var dialog = new CreateIssueDialogViewModel(_context, defaults);
        if (await _services.Dialogs.ShowDialogAsync(dialog) && dialog.CreatedId is { } id)
            ShowIssue(id);
    }

    // ---- Board menu ---------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task BoardSettingsAsync()
    {
        if (_context is null || SelectedBoard is null)
            return;
        await _services.Dialogs.ShowDialogAsync(new BoardSettingsDialogViewModel(_context, SelectedBoard.Id));
    }

    [RelayCommand]
    private void ProjectSettings() => OpenDocument?.Invoke(ProjectSettingsDocumentFactory.IdFor(Location));

    [RelayCommand]
    private async Task NewBoardViewAsync()
    {
        if (_context is null)
            return;
        var dialog = new TextInputDialogViewModel("New Board", "Name", $"{_context.Project.Key} board {Boards.Count + 1}", "Create")
        {
            Choice = new ChoiceInput("Kind", ["Kanban", "Scrum"], "Kanban"),
        };
        if (!await _services.Dialogs.ShowDialogAsync(dialog))
            return;
        var id = BoardIds.New();
        var kind = dialog.Choice!.Selected == "Scrum" ? BoardKind.Scrum : BoardKind.Kanban;
        if (await _context.RunAsync(new CreateBoardView(dialog.Text, kind) { Id = id }))
            SelectedBoard = Boards.FirstOrDefault(b => b.Id == id) ?? SelectedBoard;
    }

    [RelayCommand]
    private async Task DeleteBoardViewAsync()
    {
        if (_context is null || SelectedBoard is not { } board)
            return;
        var ok = await _services.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Delete Board",
            $"Delete the board \"{board.Name}\"? Its columns, filter and quick filters go; the issues stay.", "Delete", [board.Name]));
        if (ok)
            await _context.RunAsync(new DeleteBoardView(board.Id));
    }

    [RelayCommand]
    private async Task CopyBoardAsync()
    {
        if (_context is not null)
            await _services.Opener.CopyBoardAsync(_context);
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (_context is not null)
            await _context.UndoAsync();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (_context is not null)
            await _context.ReloadAsync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var page in _pageCache.Values)
            page.Dispose();
        _pageCache.Clear();
        IssuePanel = null;
        if (_context is not null)
        {
            _context.Changed -= OnBoardChanged;
            _services.Boards.Release(_context);
            _context = null;
        }
    }
}

/// <summary>Creates board tabs from ids like "Board:folder|C:\Boards\DEV" (opening, and restoring at startup).</summary>
public sealed class BoardDocumentFactory(BoardUiServices services) : IDocumentFactory
{
    public string IdPrefix => BoardDocumentViewModel.IdPrefix;

    public static string IdFor(BoardLocation location) => BoardDocumentViewModel.IdPrefix + location.Key;

    public DocumentViewModelBase? Create(string id)
    {
        if (!id.StartsWith(IdPrefix, StringComparison.Ordinal) || BoardLocation.FromKey(id[IdPrefix.Length..]) is not { } location)
            return null;
        if (services.Boards.Provider(location.StoreKind) is null)
            return null;   // its store's plugin is off: skipped, comes back when it's on
        return new BoardDocumentViewModel(location, services);
    }
}
