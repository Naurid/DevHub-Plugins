using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board;
using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Controls;
using DevHub.Services.Board;
using DevHub.ViewModels.Dialogs;
using DevHub.Sdk.Core.Localization;

namespace DevHub.ViewModels.Board;

/// <summary>The Board page: the board's columns with their cards (in swimlanes when chosen), drag and drop, quick filters.</summary>
public sealed class BoardViewPageProvider : IBoardPageProvider
{
    public string Id => "board";
    public string Title => L.T("Board");
    public int Order => 100;
    public BoardPageViewModel Create(BoardPageHost host) => new BoardViewPageViewModel(host);
}

/// <summary>A quick filter button above the board.</summary>
public sealed partial class QuickFilterToggle(string id, string name, Action changed) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isOn;
    partial void OnIsOnChanged(bool value) => changed();
    public override string ToString() => Name;
}

/// <summary>An assignee in the avatar row above the board (click: only their cards).</summary>
public sealed partial class AssigneeToggle(string id, string name, Action changed) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isOn;
    partial void OnIsOnChanged(bool value) => changed();
    public override string ToString() => Name;
}

/// <summary>A column header: name, card count, WIP limits, and its menu.</summary>
public sealed class ColumnViewModel(BoardViewPageViewModel page, ColumnLayout layout, int index, int columnCount) : ViewModelBase
{
    public BoardColumn Column { get; } = layout.Column;
    public string Id => Column.Id;
    public string Name => Column.Name;
    public int Index { get; } = index;
    public int Count { get; } = layout.Cards.Count;
    public bool IsOverLimit => layout.OverLimit;
    public bool IsUnderMinimum => layout.UnderMinimum;
    public bool IsDoneColumn => layout.Category == StatusCategory.Done;
    public bool HasNoStatus => Column.Statuses.Count == 0;

    public string CountText => Column.WipLimit is { } max
        ? $"{Count} / {max}"
        : Column.WipMinimum is { } min ? L.T($"{Count} (min {min})") : Count.ToString(CultureInfo.CurrentCulture);

    public string ToolTip => HasNoStatus
        ? L.T("No status yet: cards can't be dropped here. Use Statuses… in the column menu.")
        : L.T("Statuses: ") + string.Join(", ", Column.Statuses.Select(page.Context.StatusName));

    public bool CanMoveLeft => Index > 0;
    public bool CanMoveRight => Index < columnCount - 1;

    public IAsyncRelayCommand RenameCommand => new AsyncRelayCommand(() => page.RenameColumnAsync(this));
    public IAsyncRelayCommand WipCommand => new AsyncRelayCommand(() => page.SetWipAsync(this));
    public IAsyncRelayCommand StatusesCommand => new AsyncRelayCommand(() => page.ColumnStatusesAsync(this));
    public IAsyncRelayCommand MergeCommand => new AsyncRelayCommand(() => page.MergeColumnAsync(this));
    public IAsyncRelayCommand DeleteCommand => new AsyncRelayCommand(() => page.DeleteColumnAsync(this));
    public IAsyncRelayCommand MoveLeftCommand => new AsyncRelayCommand(() => page.MoveColumnAsync(this, Index - 1));
    public IAsyncRelayCommand MoveRightCommand => new AsyncRelayCommand(() => page.MoveColumnAsync(this, Index + 1));
    public IAsyncRelayCommand AddIssueCommand => new AsyncRelayCommand(() => page.CreateIssueInAsync(this));
    public IAsyncRelayCommand AddColumnAfterCommand => new AsyncRelayCommand(() => page.AddColumnAsync(Index + 1));

    public override string ToString() => L.T($"{Name}, {CountText} cards");
}

/// <summary>One column of one swimlane: a list of cards you can drop into.</summary>
public sealed class CellViewModel(string columnId, string laneKey, IEnumerable<IssueItemViewModel> cards) : ViewModelBase
{
    public string ColumnId { get; } = columnId;
    public string LaneKey { get; } = laneKey;
    public ObservableCollection<IssueItemViewModel> Cards { get; } = new(cards);
    public bool IsEmpty => Cards.Count == 0;
}

/// <summary>A swimlane: its title (none when the board has no swimlanes) and a cell per column.</summary>
public sealed partial class LaneViewModel(string key, string title, int count, IReadOnlyList<CellViewModel> cells) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public bool HasHeader => Title.Length > 0;
    public string CountText => count == 1 ? L.T("1 issue") : L.T($"{count} issues");
    public IReadOnlyList<CellViewModel> Cells { get; } = cells;
    [ObservableProperty] private bool _isExpanded = true;
    public override string ToString() => Title;
}

public sealed partial class BoardViewPageViewModel : BoardPageViewModel, IItemsDropHandler, ICardHost
{
    private string _search = "";

    public BoardViewPageViewModel(BoardPageHost host) : base(host)
    {
        Rebuild();
    }

    public override string Title => L.T("Board");

    System.Windows.Input.ICommand ICardHost.SelectCommand => SelectCommand;
    System.Windows.Input.ICommand ICardHost.OpenCommand => OpenCommand;
    System.Windows.Input.ICommand ICardHost.AssignToMeCommand => AssignToMeCommand;
    System.Windows.Input.ICommand ICardHost.DeleteCommand => DeleteCardCommand;
    System.Windows.Input.ICommand ICardHost.NextColumnCommand => MoveCardToNextColumnCommand;
    System.Windows.Input.ICommand ICardHost.PreviousColumnCommand => MoveCardToPreviousColumnCommand;
    System.Windows.Input.ICommand ICardHost.PickCommand => PickCommand;

    public bool HasQuickFilters => QuickFilters.Count > 0;

    public ObservableCollection<QuickFilterToggle> QuickFilters { get; } = [];
    public ObservableCollection<AssigneeToggle> Assignees { get; } = [];
    public ObservableCollection<ColumnViewModel> Columns { get; } = [];
    public ObservableCollection<LaneViewModel> Lanes { get; } = [];

    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string? _notOnBoard;
    [ObservableProperty] private string? _sprintTitle;
    [ObservableProperty] private string? _sprintDetails;
    [ObservableProperty] private bool _isScrum;
    [ObservableProperty] private bool _noActiveSprint;
    [ObservableProperty] private bool _hasNoColumns;
    [ObservableProperty] private bool _compactCards;
    [ObservableProperty] private string? _selectedIssueId;

    // Cards picked with Ctrl+click, kept across rebuilds while they're on the board.
    private readonly HashSet<string> _picked = new(StringComparer.Ordinal);

    public bool HasPicked => _picked.Count > 1;
    public string PickedText => L.T($"{_picked.Count} selected");

    public override void OnBoardChanged() => Rebuild();

    public override void OnSearch(string text)
    {
        _search = text;
        Rebuild();
    }

    private bool _rebuilding;

    /// <summary>Recomputes everything from the snapshot (quick filter and assignee choices are kept).</summary>
    public void Rebuild()
    {
        if (_rebuilding)
            return;
        _rebuilding = true;
        try
        {
            var snapshot = Context.Snapshot;
            var definition = snapshot.Boards.GetValueOrDefault(Host.Definition.Id) ?? Host.Definition;
            CompactCards = Context.Service.CompactCards;
            IsScrum = definition.Kind == BoardKind.Scrum;

            var onFilters = QuickFilters.Where(q => q.IsOn).Select(q => q.Id).ToHashSet(StringComparer.Ordinal);
            QuickFilters.Clear();
            foreach (var q in definition.QuickFilters)
                QuickFilters.Add(new QuickFilterToggle(q.Id, q.Name, Rebuild) { IsOn = onFilters.Contains(q.Id) });
            OnPropertyChanged(nameof(HasQuickFilters));

            var onPeople = Assignees.Where(a => a.IsOn).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            var options = new BoardViewOptions
            {
                QuickFilters = onFilters,
                Search = _search,
                Assignees = onPeople,
            };
            var layout = BoardLayout.Build(snapshot, definition, options, Context.QueryContext());

            // The avatar row: everyone with a card on the board (before the assignee filter narrows it).
            var withCards = BoardLayout.Build(snapshot, definition, options with { Assignees = new HashSet<string>() }, Context.QueryContext())
                .Columns.SelectMany(c => c.Cards).Select(i => i.Assignee).OfType<string>().Distinct().ToList();
            Assignees.Clear();
            foreach (var id in withCards.OrderBy(Context.PersonName, StringComparer.CurrentCultureIgnoreCase))
                Assignees.Add(new AssigneeToggle(id, Context.PersonName(id), Rebuild) { IsOn = onPeople.Contains(id) });

            Problem = layout.Problem;
            NotOnBoard = layout.NotOnBoard.Count == 0
                ? null
                : L.T("Not on this board: ") + string.Join(", ", layout.NotOnBoard.Select(n => $"{n.Status.Name} ({n.Count})"));
            HasNoColumns = definition.Columns.Count == 0;

            var sprint = layout.ActiveSprints.FirstOrDefault();
            NoActiveSprint = IsScrum && sprint is null;
            SprintTitle = sprint is null ? null : string.Join(" + ", layout.ActiveSprints.Select(s => s.Name));
            SprintDetails = sprint is null ? null : SprintText(sprint, layout);

            Columns.Clear();
            for (var i = 0; i < layout.Columns.Count; i++)
                Columns.Add(new ColumnViewModel(this, layout.Columns[i], i, layout.Columns.Count));

            Lanes.Clear();
            foreach (var lane in layout.Swimlanes)
            {
                var cells = layout.Columns.Select((c, i) => new CellViewModel(c.Column.Id, lane.Key,
                    lane.Cells[i].Select(issue => new IssueItemViewModel(issue, snapshot, Context, definition.CardFields) { Host = this }))).ToList();
                Lanes.Add(new LaneViewModel(lane.Key, lane.Title, lane.Count, cells));
            }
            _picked.IntersectWith(AllCards.Select(c => c.Id));
            ShowPicks();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private static string SprintText(Sprint sprint, BoardLayoutResult layout)
    {
        var parts = new List<string>();
        if (sprint.End is { } end)
        {
            var days = (int)Math.Ceiling((end - DateTimeOffset.Now).TotalDays);
            parts.Add(days switch { < 0 => L.T($"ended {-days} days ago"), 0 => L.T("ends today"), 1 => L.T("1 day left"), _ => L.T($"{days} days left") });
        }
        var all = layout.Columns.SelectMany(c => c.Cards).ToList();
        var done = layout.Columns.Where(c => c.Category == StatusCategory.Done).SelectMany(c => c.Cards).ToList();
        parts.Add(L.T($"{done.Sum(i => i.StoryPoints ?? 0):0.##} of {all.Sum(i => i.StoryPoints ?? 0):0.##} points done"));
        if (sprint.Goal.Length > 0)
            parts.Add(L.T("Goal: ") + sprint.Goal);
        return string.Join(" · ", parts);
    }

    // ---- Cards --------------------------------------------------------------------------------------------

    private IEnumerable<IssueItemViewModel> AllCards => Lanes.SelectMany(l => l.Cells).SelectMany(c => c.Cards);

    [RelayCommand]
    private void Select(IssueItemViewModel? card)
    {
        _picked.Clear();
        ShowPicks();
        SelectedIssueId = card?.Id;
        Host.SelectIssue(card?.Id);
    }

    /// <summary>Ctrl+click: the card joins the selection (with the one selected before it), or leaves it.</summary>
    [RelayCommand]
    private void Pick(IssueItemViewModel? card)
    {
        if (card is null)
            return;
        if (_picked.Count == 0 && SelectedIssueId is { } selected && selected != card.Id && AllCards.Any(c => c.Id == selected))
            _picked.Add(selected);
        if (!_picked.Add(card.Id))
            _picked.Remove(card.Id);
        ShowPicks();
        SelectedIssueId = card.Id;
        Host.SelectIssue(card.Id);
    }

    [RelayCommand]
    private void ClearPicks()
    {
        _picked.Clear();
        ShowPicks();
    }

    [RelayCommand]
    private async Task EditPickedAsync()
    {
        if (_picked.Count > 0)
            await Context.Dialogs.ShowDialogAsync(new BulkEditDialogViewModel(Context, [.. PickedInOrder().Select(p => p.Card.Id)]));
    }

    private void ShowPicks()
    {
        foreach (var card in AllCards)
            card.IsPicked = _picked.Contains(card.Id);
        OnPropertyChanged(nameof(HasPicked));
        OnPropertyChanged(nameof(PickedText));
    }

    // The picked cards as the board shows them: column by column, lane by lane, top to bottom.
    private List<(IssueItemViewModel Card, CellViewModel Cell)> PickedInOrder() =>
        Columns.SelectMany(column => Lanes.SelectMany(l => l.Cells).Where(c => c.ColumnId == column.Id))
            .SelectMany(cell => cell.Cards.Where(c => _picked.Contains(c.Id)).Select(c => (c, cell))).ToList();

    [RelayCommand]
    private void Open(IssueItemViewModel? card)
    {
        if (card is not null)
            Host.OpenIssue(card.Id);
    }

    [RelayCommand]
    private async Task AssignToMeAsync(IssueItemViewModel? card)
    {
        if (card is null)
            return;
        if (Context.Me is null)
            await Context.RunAsync(new AddPerson(Context.Service.User.Name, Context.Service.User.Email));
        if (Context.Me is { } me)
            await Context.RunAsync(new EditIssue(card.Id, IssueFields.Assignee, new FieldValue.Text(me)));
    }

    [RelayCommand]
    private async Task DeleteCardAsync(IssueItemViewModel? card)
    {
        if (card is not null)
            await IssueCommands.DeleteAsync(Context, card.Id);
    }

    [RelayCommand]
    private async Task MoveCardToNextColumnAsync(IssueItemViewModel? card) => await MoveCardByAsync(card, 1);

    [RelayCommand]
    private async Task MoveCardToPreviousColumnAsync(IssueItemViewModel? card) => await MoveCardByAsync(card, -1);

    private async Task MoveCardByAsync(IssueItemViewModel? card, int step)
    {
        if (card is null)
            return;
        var index = Columns.ToList().FindIndex(c => c.Column.Statuses.Contains(card.Issue.Status));
        var target = index + step;
        if (index < 0 || target < 0 || target >= Columns.Count)
            return;
        await Context.RunAsync(new MoveCard(Host.Definition.Id, card.Id, Columns[target].Id, null, null));
    }

    [RelayCommand]
    private async Task AddColumnAtEndAsync() => await AddColumnAsync(Columns.Count);

    [RelayCommand]
    private void ClearFilters()
    {
        // Every toggle off first, then one rebuild: a rebuild replaces the toggle lists, so rebuilding after each
        // one changed the list being walked (two people filtered + Clear filters crashed).
        _rebuilding = true;
        try
        {
            foreach (var q in QuickFilters.ToList())
                q.IsOn = false;
            foreach (var a in Assignees.ToList())
                a.IsOn = false;
        }
        finally
        {
            _rebuilding = false;
        }
        Rebuild();
    }

    // ---- Drag and drop ------------------------------------------------------------------------------------

    public bool CanDrop(ItemsDrop drop) => drop switch
    {
        { Item: ColumnViewModel, TargetList: ObservableCollection<ColumnViewModel> } => true,
        { Item: IssueItemViewModel card, TargetList: CellViewModel cell } => CanDropCard(card, cell),
        _ => false,
    };

    private bool CanDropCard(IssueItemViewModel card, CellViewModel cell)
    {
        var column = Columns.FirstOrDefault(c => c.Id == cell.ColumnId)?.Column;
        if (column is null)
            return false;
        var issue = Context.Snapshot.Issue(card.Id);
        return issue is not null && (column.Statuses.Contains(issue.Status) || MoveCard.TargetStatus(Context.Snapshot, issue, column) is not null);
    }

    public void Drop(ItemsDrop drop) => _ = DropAsync(drop);

    private async Task DropAsync(ItemsDrop drop)
    {
        switch (drop)
        {
            case { Item: ColumnViewModel column }:
            {
                var index = drop.Index > column.Index ? drop.Index - 1 : drop.Index;
                if (index != column.Index)
                    await Context.RunAsync(new MoveColumn(Host.Definition.Id, column.Id, index));
                break;
            }
            case { Item: IssueItemViewModel card, TargetList: CellViewModel cell }:
                await DropCardsAsync(drop, card, cell);
                break;
        }
    }

    // Dragging a picked card moves every picked card that may go there, in board order, together where it's dropped.
    private async Task DropCardsAsync(ItemsDrop drop, IssueItemViewModel card, CellViewModel cell)
    {
        var group = _picked.Contains(card.Id) && _picked.Count > 1
            ? PickedInOrder()
            : [(card, drop.SourceList as CellViewModel ?? cell)];
        var moving = group.Where(g => g.Card == card || CanDropCard(g.Card, cell)).ToList();
        var ids = moving.Select(g => g.Card.Id).ToHashSet(StringComparer.Ordinal);

        // The neighbours of the whole group, none of them moving; the drop index counts the moving cards above it.
        var others = cell.Cards.Where(c => !ids.Contains(c.Id)).ToList();
        var index = drop.Index - cell.Cards.Take(Math.Max(drop.Index, 0)).Count(c => ids.Contains(c.Id));
        index = Math.Clamp(index, 0, others.Count);
        var above = index > 0 ? others[index - 1].Id : null;
        var below = index < others.Count ? others[index].Id : null;

        var steps = new List<IBoardOperation>();
        foreach (var (each, from) in moving)
        {
            steps.Add(new MoveCard(Host.Definition.Id, each.Id, from.ColumnId == cell.ColumnId ? null : cell.ColumnId, above, below));
            if (from.LaneKey != cell.LaneKey && ChangeLane(each.Id, cell.LaneKey) is { } lane)
                steps.Add(lane);
            above = each.Id;   // the next one goes below it
        }
        if (steps.Count == 1)
            await Context.RunAsync(steps[0]);
        else if (await Context.RunAsync(new Batch(steps, L.T($"{moving.Count} cards moved to {ColumnName(cell.ColumnId)}")))
                 && moving.Count < group.Count)
            Context.Status.Show(L.T($"{moving.Count} cards moved; {group.Count - moving.Count} can't go to {ColumnName(cell.ColumnId)} (their workflow doesn't allow it)."));
    }

    private string ColumnName(string columnId) => Columns.FirstOrDefault(c => c.Id == columnId)?.Name ?? columnId;

    // Dropping into another swimlane sets what the lanes group by (assignee, epic, priority, type).
    private IBoardOperation? ChangeLane(string issueId, string laneKey)
    {
        var value = laneKey.Length == 0 ? null : laneKey;
        var field = Host.Definition.Swimlanes switch
        {
            SwimlaneMode.Assignee => IssueFields.Assignee,
            SwimlaneMode.Priority => IssueFields.Priority,
            SwimlaneMode.Type => value is null ? null : IssueFields.Type,
            SwimlaneMode.Epic => Context.Snapshot.Issue(issueId) is { } i && Context.Snapshot.LevelOf(i) == IssueTypeLevel.Standard ? IssueFields.Parent : null,
            _ => null,
        };
        return field is null ? null : new EditIssue(issueId, field, new FieldValue.Text(value));
    }

    // ---- Column menu --------------------------------------------------------------------------------------

    internal async Task RenameColumnAsync(ColumnViewModel column)
    {
        var dialog = new TextInputDialogViewModel(L.T("Rename Column"), L.T("Name"), column.Name, L.T("Rename"));
        if (await Context.Dialogs.ShowDialogAsync(dialog))
            await Context.RunAsync(new RenameColumn(Host.Definition.Id, column.Id, dialog.Text));
    }

    internal async Task SetWipAsync(ColumnViewModel column)
    {
        var dialog = new TextInputDialogViewModel(L.T("Column Limits"), L.T("Most cards (empty = no limit)"),
            column.Column.WipLimit?.ToString(CultureInfo.CurrentCulture) ?? "", L.T("Save"))
        {
            AllowEmpty = true,
            SecondLabel = L.T("Fewest cards (empty = no minimum)"),
            SecondText = column.Column.WipMinimum?.ToString(CultureInfo.CurrentCulture) ?? "",
            Explanation = L.T("The column's header turns red when it holds more cards than the limit, or fewer than the minimum."),
        };
        if (!await Context.Dialogs.ShowDialogAsync(dialog))
            return;
        int? Parse(string text) => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out var n) ? n : null;
        await Context.RunAsync(new SetWipLimits(Host.Definition.Id, column.Id, Parse(dialog.SecondText), Parse(dialog.Text)));
    }

    internal async Task ColumnStatusesAsync(ColumnViewModel column)
    {
        var definition = Host.Definition;
        var items = Context.Project.Statuses.Select(s =>
        {
            var holder = definition.ColumnOf(s.Id);
            var note = holder is null ? L.T("not on this board") : holder.Id == column.Id ? null : L.T("now in ") + holder.Name;
            return new ChecklistItem(s.Id, s.Name, column.Column.Statuses.Contains(s.Id), note);
        }).ToList();
        var dialog = new ChecklistDialogViewModel(L.T($"Statuses in {column.Name}"),
            L.T("Cards in any of these statuses show in this column. A status can be in one column of a board; choosing it here takes it out of the other."),
            items);
        if (await Context.Dialogs.ShowDialogAsync(dialog))
            await Context.RunAsync(new SetColumnStatuses(definition.Id, column.Id, dialog.CheckedIds));
    }

    internal async Task MergeColumnAsync(ColumnViewModel column)
    {
        var others = Columns.Where(c => c.Id != column.Id).ToList();
        if (others.Count == 0)
            return;
        var dialog = new TextInputDialogViewModel(L.T($"Merge {column.Name}"), "", "", L.T("Merge"))
        {
            ShowText = false,
            Explanation = L.T($"{column.Name} disappears; its statuses (and their cards) join the column you choose."),
            Choice = new ChoiceInput(L.T("Into"), others.Select(c => c.Name).ToList(), others[Math.Max(0, column.Index - 1) % others.Count].Name),
        };
        if (!await Context.Dialogs.ShowDialogAsync(dialog))
            return;
        var into = others.First(c => c.Name == dialog.Choice!.Selected);
        await Context.RunAsync(new MergeColumns(Host.Definition.Id, column.Id, into.Id));
    }

    internal async Task DeleteColumnAsync(ColumnViewModel column)
    {
        var statuses = column.Column.Statuses.Select(Context.StatusName).ToList();
        var targets = Context.Project.Statuses.Where(s => !column.Column.Statuses.Contains(s.Id)).ToList();
        const string keep = "Keep them (their cards are listed as not on this board)";
        const string delete = "Delete them everywhere";
        var dialog = new TextInputDialogViewModel(L.T($"Delete {column.Name}"), "", "", L.T("Delete"))
        {
            ShowText = false,
            Destructive = true,
            Explanation = statuses.Count == 0
                ? L.T($"The column {column.Name} has no statuses.")
                : L.T($"The column holds {string.Join(", ", statuses)}. What happens to those statuses?"),
            Choice = statuses.Count == 0 ? null : new ChoiceInput(L.T("Statuses"), [keep, delete], keep),
            SecondChoice = statuses.Count == 0 || targets.Count == 0 ? null
                : new ChoiceInput(L.T("Move their issues to"), targets.Select(t => t.Name).ToList(), targets[0].Name),
        };
        if (!await Context.Dialogs.ShowDialogAsync(dialog))
            return;
        var deleteStatuses = dialog.Choice?.Selected == delete;
        var target = deleteStatuses ? targets.FirstOrDefault(t => t.Name == dialog.SecondChoice?.Selected)?.Id : null;
        await Context.RunAsync(new DeleteColumn(Host.Definition.Id, column.Id) { DeleteStatuses = deleteStatuses, MoveIssuesTo = target });
    }

    internal async Task MoveColumnAsync(ColumnViewModel column, int index) =>
        await Context.RunAsync(new MoveColumn(Host.Definition.Id, column.Id, index));

    [RelayCommand]
    private async Task CreateIssueInColumnAsync(string? columnId)
    {
        if (Columns.FirstOrDefault(c => c.Id == columnId) is { } column)
            await CreateIssueInAsync(column);
    }

    internal async Task CreateIssueInAsync(ColumnViewModel column)
    {
        var status = column.Column.Statuses.FirstOrDefault();
        var sprint = Context.Snapshot.Sprints.Values.FirstOrDefault(s => s.State == SprintState.Active && IsScrum)?.Id;
        await Host.CreateIssue(new CreateIssueDefaults { Status = status, Sprint = sprint });
    }

    internal async Task AddColumnAsync(int index)
    {
        var dialog = new TextInputDialogViewModel(L.T("Add Column"), L.T("Name"), "", L.T("Add"))
        {
            Explanation = L.T("A new column gets a status of the same name (or takes an existing status that isn't on this board yet)."),
            Choice = new ChoiceInput(L.T("Counts as"), [L.T("To Do"), L.T("In Progress"), L.T("Done")], L.T("In Progress")),
        };
        if (!await Context.Dialogs.ShowDialogAsync(dialog))
            return;
        var category = dialog.Choice!.SelectedIndex switch { 0 => StatusCategory.ToDo, 2 => StatusCategory.Done, _ => StatusCategory.InProgress };   // To Do, In Progress, Done
        await Context.RunAsync(new AddColumn(Host.Definition.Id, dialog.Text, category) { Index = index });
    }
}
