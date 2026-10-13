using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board;
using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Search;
using DevHub.Controls;
using DevHub.Services.Board;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Board;

/// <summary>The Backlog page: the ranked list of open issues, an epics panel, a version filter, bulk edit.</summary>
public sealed class BacklogPageProvider : IBoardPageProvider
{
    public const string PageId = "backlog";
    public string Id => PageId;
    public string Title => "Backlog";
    public int Order => 200;
    public BoardPageViewModel Create(BoardPageHost host) => new BacklogPageViewModel(host);
}

/// <summary>An epic in the epics panel: its progress and points.</summary>
public sealed record EpicRow(string? Id, string Key, string Summary, string Progress, double Done, bool IsSpecial)
{
    public override string ToString() => IsSpecial ? Summary : $"{Key} {Summary}";
}

/// <summary>
/// What the backlog and Board.Scrum's planning page share: the epics panel, the version filter, the search, the
/// quick-create row, bulk edit and ranking by drag and drop. A page subclasses it and says which lists it shows.
/// </summary>
public abstract partial class PlanningPageViewModel : BoardPageViewModel, IItemsDropHandler, ICardHost
{
    private string _search = "";

    System.Windows.Input.ICommand ICardHost.SelectCommand => SelectCommand;
    System.Windows.Input.ICommand ICardHost.OpenCommand => OpenCommand;
    System.Windows.Input.ICommand ICardHost.DeleteCommand => DeleteCommand;
    System.Windows.Input.ICommand ICardHost.MoveToTopCommand => MoveToTopCommand;
    System.Windows.Input.ICommand ICardHost.MoveToBottomCommand => MoveToBottomCommand;

    protected PlanningPageViewModel(BoardPageHost host) : base(host)
    {
    }

    public ObservableCollection<EpicRow> Epics { get; } = [];
    [ObservableProperty] private EpicRow? _selectedEpic;
    [ObservableProperty] private bool _showEpics = true;

    public ObservableCollection<NamedItem> VersionFilters { get; } = [];
    [ObservableProperty] private NamedItem? _versionFilter;

    public ObservableCollection<IssueListViewModel> Lists { get; } = [];

    [ObservableProperty] private string _newSummary = "";
    public ObservableCollection<NamedItem> NewTypes { get; } = [];
    [ObservableProperty] private NamedItem? _newType;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private bool _showDone;


    /// <summary>Adds a list to the page; selecting in it shows the issue in the panel.</summary>
    protected void AddList(IssueListViewModel list)
    {
        Lists.Add(list);
        list.Selected.CollectionChanged += OnListSelectionChanged;
    }

    /// <summary>Removes a list (Board.Scrum: a sprint that closed).</summary>
    protected void RemoveList(IssueListViewModel list)
    {
        list.Selected.CollectionChanged -= OnListSelectionChanged;
        Lists.Remove(list);
    }

    private void OnListSelectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_rebuilding || _filling)
            return;
        OnPropertyChanged(nameof(SelectedIssues));
        if (e.NewItems is { Count: > 0 } added && added[^1] is IssueItemViewModel item)
            Host.SelectIssue(item.Id);
    }

    private bool _filling;

    public override void Dispose()
    {
        foreach (var list in Lists)
            list.Selected.CollectionChanged -= OnListSelectionChanged;
        base.Dispose();
    }
    /// <summary>Every issue selected in any list.</summary>
    public IReadOnlyList<IssueItemViewModel> SelectedIssues => Lists.SelectMany(l => l.Selected).DistinctBy(i => i.Id).ToList();

    public override void OnBoardChanged() => Rebuild();

    public override void OnSearch(string text)
    {
        _search = text;
        Rebuild();
    }

    partial void OnSelectedEpicChanged(EpicRow? value) => RebuildLists();
    partial void OnVersionFilterChanged(NamedItem? value) => RebuildLists();
    partial void OnShowDoneChanged(bool value) => RebuildLists();

    private bool _rebuilding;

    protected void Rebuild()
    {
        if (_rebuilding)
            return;
        _rebuilding = true;
        try
        {
            var board = Context.Snapshot;
            var project = board.Project;

            var epicId = SelectedEpic?.Id;
            var epicSpecial = SelectedEpic?.IsSpecial == true ? SelectedEpic.Key : null;
            Epics.Clear();
            Epics.Add(new EpicRow(null, "all", "All issues", "", 0, true));
            Epics.Add(new EpicRow(null, "none", "Issues without an epic", "", 0, true));
            foreach (var epic in board.Issues.Values.Where(i => board.LevelOf(i) == IssueTypeLevel.Epic && (!board.IsDone(i) || ShowDone))
                         .OrderBy(i => i.Rank, StringComparer.Ordinal))
            {
                var children = board.ChildrenOf(epic.Id);
                var done = children.Count(board.IsDone);
                var points = children.Sum(c => c.StoryPoints ?? 0);
                Epics.Add(new EpicRow(epic.Id, epic.Key, epic.Summary,
                    children.Count == 0 ? "no issues" : $"{done}/{children.Count} done · {points.ToString("0.##", CultureInfo.CurrentCulture)} pts",
                    children.Count == 0 ? 0 : (double)done / children.Count, false));
            }
            _suppress = true;
            SelectedEpic = Epics.FirstOrDefault(e => epicSpecial is not null ? e.IsSpecial && e.Key == epicSpecial : !e.IsSpecial && e.Id == epicId) ?? Epics[0];

            var versionId = VersionFilter?.Id;
            VersionFilters.Clear();
            VersionFilters.Add(new NamedItem("", "All versions"));
            VersionFilters.Add(new NamedItem("-", "No version"));
            foreach (var v in board.Versions.Values.Where(v => !v.Archived).OrderBy(v => v.Release ?? DateOnly.MaxValue))
                VersionFilters.Add(new NamedItem(v.Id, v.Name));
            VersionFilter = VersionFilters.FirstOrDefault(v => v.Id == versionId) ?? VersionFilters[0];

            var type = NewType?.Id;
            NewTypes.Clear();
            foreach (var t in project.IssueTypes.Where(t => t.Level == IssueTypeLevel.Standard))
                NewTypes.Add(new NamedItem(t.Id, t.Name, t.Icon));
            NewType = NewTypes.FirstOrDefault(t => t.Id == type) ?? NewTypes.FirstOrDefault(t => t.Id == "story") ?? NewTypes.FirstOrDefault();
            _suppress = false;

            RebuildLists();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private bool _suppress;

    private void RebuildLists()
    {
        if (_suppress)
            return;
        var board = Context.Snapshot;
        Func<Issue, bool> filter;
        try
        {
            filter = QueryCompiler.Compile(Host.Definition.Filter, Context.QueryContext()).Matches;
            Problem = null;
        }
        catch (QueryException e)
        {
            filter = _ => true;
            Problem = "The board's filter has a problem: " + e.Message;
        }
        var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Shows(Issue i)
        {
            var level = board.LevelOf(i);
            if (level != IssueTypeLevel.Standard || (!ShowDone && board.IsDone(i)) || !filter(i))
                return false;
            if (SelectedEpic is { IsSpecial: true, Key: "none" } && board.EpicOf(i) is not null)
                return false;
            if (SelectedEpic is { IsSpecial: false, Id: { } epic } && board.EpicOf(i)?.Id != epic)
                return false;
            if (VersionFilter?.Id is "-" && i.FixVersions.Count > 0)
                return false;
            if (VersionFilter?.Id is { Length: > 1 } version && !i.FixVersions.Contains(version))
                return false;
            return words.All(w => i.Summary.Contains(w, StringComparison.CurrentCultureIgnoreCase) || i.Key.Contains(w, StringComparison.OrdinalIgnoreCase));
        }
        _filling = true;
        try
        {
            FillLists(board.Issues.Values.Where(Shows).ToList(), board);
        }
        finally
        {
            _filling = false;
        }
        OnPropertyChanged(nameof(SelectedIssues));
    }

    /// <summary>Puts the shown issues into the page's lists.</summary>
    protected abstract void FillLists(IReadOnlyList<Issue> issues, BoardSnapshot board);

    /// <summary>A drop between or within lists: rank it (and, in Scrum, move it to another sprint).</summary>
    protected virtual async Task DropAsync(IssueListDrop drop) =>
        await Context.RunAsync(new RankIssue(drop.Item.Id, drop.Above, drop.Below));

    /// <summary>Extra fields a quick-created issue gets in this list (Scrum: its sprint).</summary>
    protected virtual IReadOnlyList<(string Field, FieldValue Value)> FieldsForNewIssue() => [];

    public bool CanDrop(ItemsDrop drop) => drop is { Item: IssueItemViewModel, TargetList: IssueListViewModel };

    public void Drop(ItemsDrop drop)
    {
        if (IssueListDrops.From(drop) is { } listDrop)
            _ = DropAsync(listDrop);
    }

    [RelayCommand]
    private async Task QuickCreateAsync()
    {
        var summary = NewSummary.Trim();
        if (summary.Length == 0 || NewType?.Id is not { } type)
            return;
        var fields = FieldsForNewIssue().ToList();
        var parent = SelectedEpic is { IsSpecial: false, Id: { } epic } ? epic : null;
        if (VersionFilter?.Id is { Length: > 1 } version)
            fields.Add((IssueFields.FixVersions, new FieldValue.Items([version])));
        if (await Context.RunAsync(new CreateIssue(type, summary) { Parent = parent, Fields = fields }))
            NewSummary = "";
    }

    [RelayCommand]
    private void Select(IssueItemViewModel? item) => Host.SelectIssue(item?.Id);

    [RelayCommand]
    private void Open(IssueItemViewModel? item)
    {
        if (item is not null)
            Host.OpenIssue(item.Id);
    }

    [RelayCommand]
    private async Task BulkEditAsync()
    {
        var ids = SelectedIssues.Select(i => i.Id).ToList();
        if (ids.Count == 0)
        {
            Context.Status.Show("Select issues first (Ctrl+click, Shift+click).");
            return;
        }
        await Context.Dialogs.ShowDialogAsync(new BulkEditDialogViewModel(Context, ids));
    }

    [RelayCommand]
    private async Task MoveToTopAsync(IssueItemViewModel? item)
    {
        var list = Lists.FirstOrDefault(l => l.Items.Contains(item!));
        var first = list?.Items.FirstOrDefault(i => i.Id != item?.Id);
        if (item is not null && first is not null)
            await Context.RunAsync(new RankIssue(item.Id, null, first.Id));
    }

    [RelayCommand]
    private async Task MoveToBottomAsync(IssueItemViewModel? item)
    {
        var list = Lists.FirstOrDefault(l => l.Items.Contains(item!));
        var last = list?.Items.LastOrDefault(i => i.Id != item?.Id);
        if (item is not null && last is not null)
            await Context.RunAsync(new RankIssue(item.Id, last.Id, null));
    }

    [RelayCommand]
    private async Task DeleteAsync(IssueItemViewModel? item)
    {
        if (item is not null)
            await IssueCommands.DeleteAsync(Context, item.Id);
    }

    [RelayCommand]
    private async Task CreateEpicAsync() =>
        await Host.CreateIssue(new CreateIssueDefaults { Type = Context.Project.IssueTypes.FirstOrDefault(t => t.Level == IssueTypeLevel.Epic)?.Id });

    [RelayCommand]
    private void OpenEpic(EpicRow? epic)
    {
        if (epic?.Id is { } id)
            Host.SelectIssue(id);
    }
}

public sealed class BacklogPageViewModel : PlanningPageViewModel
{
    private readonly IssueListViewModel _backlog = new("backlog", "Backlog") { EmptyText = "No open issues. Create one below." };

    public BacklogPageViewModel(BoardPageHost host) : base(host)
    {
        AddList(_backlog);
        Rebuild();
    }

    public override string Title => "Backlog";

    protected override void FillLists(IReadOnlyList<Issue> issues, BoardSnapshot board) =>
        _backlog.Fill(issues, board, Context, Host.Definition.CardFields, this);
}
