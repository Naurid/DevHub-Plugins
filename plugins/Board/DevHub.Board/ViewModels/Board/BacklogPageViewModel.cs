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
using DevHub.Sdk.Core.Localization;

namespace DevHub.ViewModels.Board;

/// <summary>The Backlog page: the ranked list of open issues, an epics panel, a version filter, bulk edit.</summary>
public sealed class BacklogPageProvider : IBoardPageProvider
{
    public const string PageId = "backlog";
    public string Id => PageId;
    public string Title => L.T("Backlog");
    public int Order => 200;
    public BoardPageViewModel Create(BoardPageHost host) => new BacklogPageViewModel(host);
}

/// <summary>An epic in the epics panel: its progress and points. <see cref="Page"/> runs its menu and double-click.</summary>
public sealed record EpicRow(string? Id, string Key, string Summary, string Progress, double Done, bool IsSpecial,
    PlanningPageViewModel? Page = null)
{
    public System.Windows.Input.ICommand? OpenInTabCommand => Page is null ? null : new RelayCommand(() => Page.OpenEpicInTabCommand.Execute(this));
    public System.Windows.Input.ICommand? EditCommand => Page is null ? null : new RelayCommand(() => Page.OpenEpicCommand.Execute(this));
    public System.Windows.Input.ICommand? CreateInCommand => Page is null ? null : new RelayCommand(() => Page.CreateInEpicCommand.Execute(this));
    public System.Windows.Input.ICommand? DeleteCommand => Page is null ? null : new RelayCommand(() => Page.DeleteEpicCommand.Execute(this));

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

    /// <summary>Buttons on the page's header (Board.Scrum: Create Sprint).</summary>
    public ObservableCollection<ListAction> PageActions { get; } = [];

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
        if (_rebuilding || _filling || _clearing)
            return;
        // One list at a time: selecting in a sprint drops what was selected in the others (a selection is what a drag
        // or Edit Selected acts on, and those work on one list).
        if (e.NewItems is { Count: > 0 } && sender is ObservableCollection<IssueItemViewModel> changed)
            ClearLists(Lists.Where(l => !ReferenceEquals(l.Selected, changed)));
        OnPropertyChanged(nameof(SelectedIssues));
        OnPropertyChanged(nameof(HasSelection));
        // The issue panel opens on a tap (Tap.Command in the row), not on selection: selection happens on press,
        // and opening the panel then would move the lists under a drag that's just starting.
    }

    private bool _filling;
    private bool _clearing;

    public bool HasSelection => Lists.Any(l => l.Selected.Count > 0);

    /// <summary>Unselects every issue (Esc, or Clear Selection).</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        ClearLists(Lists);
        OnPropertyChanged(nameof(SelectedIssues));
        OnPropertyChanged(nameof(HasSelection));
    }

    private void ClearLists(IEnumerable<IssueListViewModel> lists)
    {
        _clearing = true;
        try
        {
            foreach (var list in lists.ToList())
                list.Selected.Clear();
        }
        finally
        {
            _clearing = false;
        }
    }

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
            OnPropertyChanged(nameof(CanRankByValue));
            OnPropertyChanged(nameof(RankByValueText));
            var project = board.Project;

            var epicId = SelectedEpic?.Id;
            var epicSpecial = SelectedEpic?.IsSpecial == true ? SelectedEpic.Key : null;
            Epics.Clear();
            Epics.Add(new EpicRow(null, "all", L.T("All issues"), "", 0, true));
            Epics.Add(new EpicRow(null, "none", L.T("Issues without an epic"), "", 0, true));
            foreach (var epic in board.Issues.Values.Where(i => board.LevelOf(i) == IssueTypeLevel.Epic && (!board.IsDone(i) || ShowDone))
                         .OrderBy(i => i.Rank, StringComparer.Ordinal))
            {
                var children = board.ChildrenOf(epic.Id);
                var done = children.Count(board.IsDone);
                var points = children.Sum(c => c.StoryPoints ?? 0);
                Epics.Add(new EpicRow(epic.Id, epic.Key, epic.Summary,
                    children.Count == 0 ? L.T("no issues") : L.T($"{done}/{children.Count} done · {points.ToString("0.##", CultureInfo.CurrentCulture)} pts"),
                    children.Count == 0 ? 0 : (double)done / children.Count, false, this));
            }
            _suppress = true;
            SelectedEpic = Epics.FirstOrDefault(e => epicSpecial is not null ? e.IsSpecial && e.Key == epicSpecial : !e.IsSpecial && e.Id == epicId) ?? Epics[0];

            var versionId = VersionFilter?.Id;
            VersionFilters.Clear();
            VersionFilters.Add(new NamedItem("", L.T("All versions")));
            VersionFilters.Add(new NamedItem("-", L.T("No version")));
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
            Problem = L.T("The board's filter has a problem: ") + e.Message;
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

    /// <summary>Moves one dropped issue between two others: rank it (and, in Scrum, put it in the target's sprint).</summary>
    protected virtual IBoardOperation MoveOperation(IssueListDrop drop, string issueId, string? above, string? below) =>
        new RankIssue(issueId, above, below);

    /// <summary>Before a drop runs, with the issues it moves (Scrum: says when they join the running sprint).</summary>
    protected virtual void OnDropping(IssueListDrop drop, IReadOnlyList<Issue> moving)
    {
    }

    /// <summary>Extra fields a quick-created issue gets in this list (Scrum: its sprint).</summary>
    protected virtual IReadOnlyList<(string Field, FieldValue Value)> FieldsForNewIssue() => [];

    public bool CanDrop(ItemsDrop drop) => drop is { Item: IssueItemViewModel, TargetList: IssueListViewModel };

    public void Drop(ItemsDrop drop)
    {
        // Dragging one of several selected issues moves them all, in the order they're shown, together where it's dropped.
        var selected = SelectedIssues;
        var group = drop.Item is IssueItemViewModel item && selected.Count > 1 && selected.Any(i => i.Id == item.Id)
            ? Lists.SelectMany(l => l.Items).Where(i => selected.Any(s => s.Id == i.Id)).Select(i => i.Id).Distinct().ToList()
            : drop.Item is IssueItemViewModel one ? [one.Id] : [];
        if (IssueListDrops.From(drop, group.ToHashSet(StringComparer.Ordinal)) is { } listDrop)
            _ = DropAsync(listDrop, group);
    }

    private async Task DropAsync(IssueListDrop drop, IReadOnlyList<string> ids)
    {
        var snapshot = Context.Snapshot;
        OnDropping(drop, [.. ids.Select(snapshot.Issue).OfType<Issue>()]);
        if (ids.Count == 1)
        {
            await Context.RunAsync(MoveOperation(drop, ids[0], drop.Above, drop.Below));
            return;
        }
        // Each goes below the one before it, so the group keeps its order.
        var steps = new List<IBoardOperation>();
        var above = drop.Above;
        foreach (var id in ids)
        {
            steps.Add(MoveOperation(drop, id, above, drop.Below));
            above = id;
        }
        await Context.RunAsync(new Batch(steps, L.T($"{ids.Count} issues moved to {drop.Target.Title}")));
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
        if (await Context.RunAsync(new CreateIssue(type, summary) { Parent = parent, Fields = EpicDefaults.Add(Context.Snapshot, parent, fields) }))
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

    // The number field to prioritise by: one named like "value" (the Scrum preset's Business value), else the first.
    private FieldDefinition? ValueField =>
        Context.Project.CustomFields.Where(f => f.Type == FieldType.Number)
            .OrderBy(f => f.Name.Contains("value", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();

    public bool CanRankByValue => ValueField is not null;
    public string RankByValueText => L.T($"Rank by {ValueField?.Name}");

    /// <summary>Orders every list on the page by the value field, highest first (each list among its own places).</summary>
    [RelayCommand]
    private async Task RankByValueAsync()
    {
        if (ValueField is not { } field)
            return;
        foreach (var list in Lists.ToList())
        {
            if (list.Items.Count > 1)
                await Context.RunAsync(new RankByField(list.Items.Select(i => i.Id).ToList(), IssueFields.Custom(field.Id)));
        }
    }

    [RelayCommand]
    private async Task BulkEditAsync()
    {
        var ids = SelectedIssues.Select(i => i.Id).ToList();
        if (ids.Count == 0)
        {
            Context.Status.Show(L.T("Select issues first (Ctrl+click, Shift+click)."));
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

    /// <summary>Shows the epic in the issue panel, to edit it there.</summary>
    [RelayCommand]
    private void OpenEpic(EpicRow? epic)
    {
        if (epic?.Id is { } id)
            Host.SelectIssue(id);
    }

    [RelayCommand]
    private void OpenEpicInTab(EpicRow? epic)
    {
        if (epic?.Id is { } id)
            Host.OpenIssue(id);
    }

    /// <summary>Create Issue with the epic as parent (so it starts with the epic's dates and labels).</summary>
    [RelayCommand]
    private async Task CreateInEpicAsync(EpicRow? epic)
    {
        if (epic?.Id is { } id)
            await Host.CreateIssue(new CreateIssueDefaults { Parent = id });
    }

    [RelayCommand]
    private async Task DeleteEpicAsync(EpicRow? epic)
    {
        if (epic?.Id is { } id)
            await IssueCommands.DeleteAsync(Context, id);
    }
}

public sealed class BacklogPageViewModel : PlanningPageViewModel
{
    private readonly IssueListViewModel _backlog = new("backlog", L.T("Backlog")) { EmptyText = L.T("No open issues. Create one below.") };

    public BacklogPageViewModel(BoardPageHost host) : base(host)
    {
        AddList(_backlog);
        Rebuild();
    }

    public override string Title => L.T("Backlog");

    protected override void FillLists(IReadOnlyList<Issue> issues, BoardSnapshot board) =>
        _backlog.Fill(issues, board, Context, Host.Definition.CardFields, this);
}
