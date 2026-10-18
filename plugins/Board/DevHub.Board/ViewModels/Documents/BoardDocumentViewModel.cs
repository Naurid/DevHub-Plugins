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
using DevHub.Sdk.Core.Localization;

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

    /// <summary>Several selected issues edited together (null when fewer than two are selected).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultiPanel))]
    [NotifyPropertyChangedFor(nameof(HasSidePanel))]
    private MultiIssuePanelViewModel? _multiPanel;

    public bool HasMultiPanel => MultiPanel is not null;

    /// <summary>The right pane holds something; without it the pane and its splitter go away.</summary>
    public bool HasSidePanel => HasIssuePanel || HasMultiPanel;

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
                Message = L.T($"There's no board in {Location.Location} yet.");
                CanCreateHere = true;
                return;
            }
            _context.Changed += OnBoardChanged;
            OnPropertyChanged(nameof(Context));
            ShowBoard();
        }
        catch (Exception e) when (e is BoardStoreException or IOException or UnauthorizedAccessException)
        {
            Message = L.T("The board can't be opened: ") + e.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void ShowBoard()
    {
        var context = _context!;
        _title = context.Project.Key + L.T(" board");
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
            SelectIssues = ShowIssues,
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
        OnPropertyChanged(nameof(HasSidePanel));
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
        MultiPanel?.Refresh();
        var key = _context.Project.Key + L.T(" board");
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
        MultiPanel = null;
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

    /// <summary>Several issues in the panel, edited together; fewer than two closes that view.</summary>
    public void ShowIssues(IReadOnlyList<string> issueIds)
    {
        if (_context is null || issueIds.Count < 2)
        {
            MultiPanel = null;
            return;
        }
        IssuePanel = null;
        if (MultiPanel is { } panel)
            panel.Show(issueIds);
        else
            MultiPanel = new MultiIssuePanelViewModel(_context, issueIds, () => MultiPanel = null);
    }

    public void OpenIssue(string issueId) => OpenDocument?.Invoke(IssueDocumentFactory.IdFor(Location, issueId));

    /// <summary>Shows the Search page with a query run (My Open Issues).</summary>
    public void ShowSearch(string query)
    {
        SelectedPage = Pages.FirstOrDefault(p => p.Id == SearchPageProvider.PageId) ?? SelectedPage;
        if (Page is SearchPageViewModel search)
        {
            search.Query = query;
            search.Run();
        }
    }

    /// <summary>Opens the issue with this key (also an old key) in its own tab; false when there's none.</summary>
    public bool GoToIssue(string key)
    {
        if (_context?.Snapshot.IssueByKey(key.Trim()) is not { } issue)
            return false;
        OpenIssue(issue.Id);
        return true;
    }

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
        var dialog = new TextInputDialogViewModel(L.T("New Board"), L.T("Name"), L.T($"{_context.Project.Key} board {Boards.Count + 1}"), L.T("Create"))
        {
            Choice = new ChoiceInput(L.T("Kind"), [L.T("Kanban"), L.T("Scrum")], L.T("Kanban")),
        };
        if (!await _services.Dialogs.ShowDialogAsync(dialog))
            return;
        var id = BoardIds.New();
        var kind = dialog.Choice!.SelectedIndex == 1 ? BoardKind.Scrum : BoardKind.Kanban;   // Kanban, Scrum
        if (await _context.RunAsync(new CreateBoardView(dialog.Text, kind) { Id = id }))
            SelectedBoard = Boards.FirstOrDefault(b => b.Id == id) ?? SelectedBoard;
    }

    [RelayCommand]
    private async Task DeleteBoardViewAsync()
    {
        if (_context is null || SelectedBoard is not { } board)
            return;
        var ok = await _services.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel(L.T("Delete Board"),
            L.T($"Delete the board \"{board.Name}\"? Its columns, filter and quick filters go; the issues stay."), L.T("Delete"), [board.Name]));
        if (ok)
            await _context.RunAsync(new DeleteBoardView(board.Id));
    }

    [RelayCommand]
    private async Task ImportIssuesAsync()
    {
        if (_context is not null)
            await _services.Dialogs.ShowDialogAsync(new ImportDialogViewModel(_context, _services.GitHub));
    }

    [RelayCommand]
    private Task ExportCsvAsync() => ExportAsync("csv", L.T("CSV files"), c => DevHub.Board.Core.Import.BoardExport.ToCsv(c.Snapshot));

    [RelayCommand]
    private Task ExportJsonAsync() => ExportAsync("json", L.T("JSON files"), c => DevHub.Board.Core.Import.BoardExport.ToJson(c.Snapshot));

    private async Task ExportAsync(string extension, string filter, Func<BoardContext, string> content)
    {
        if (_context is null)
            return;
        var path = await _services.Dialogs.PickSaveFileAsync(L.T("Export issues"), $"{_context.Project.Key.ToLowerInvariant()}-issues.{extension}", filter, extension);
        if (path is null)
            return;
        try
        {
            await File.WriteAllTextAsync(path, content(_context), new System.Text.UTF8Encoding(false));
            _services.Status.Show(L.T($"Exported {_context.Snapshot.Issues.Count} issues to {path}."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _services.Status.Show(L.T("The export couldn't be written: ") + e.Message);
        }
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
