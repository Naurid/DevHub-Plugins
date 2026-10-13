using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Search;
using DevHub.Services.Board;

namespace DevHub.ViewModels.Dialogs;

/// <summary>A quick filter being edited in Board Settings.</summary>
public sealed partial class QuickFilterRow(string? id, string name, string query) : ObservableObject
{
    public string? Id { get; } = id;
    [ObservableProperty] private string _name = name;
    [ObservableProperty] private string _query = query;
    [ObservableProperty] private string? _error;
}

/// <summary>
/// Board Settings (one board definition): name, kind, which issues it shows (a search query), swimlanes, what
/// hides, card fields, and the quick filters. Columns are shaped right on the board. Saved on OK.
/// </summary>
public sealed partial class BoardSettingsDialogViewModel : DialogViewModelBase
{
    private readonly BoardContext _context;
    private readonly BoardDefinition _definition;

    public BoardSettingsDialogViewModel(BoardContext context, string boardId)
    {
        _context = context;
        _definition = context.Snapshot.Boards[boardId];
        _name = _definition.Name;
        _isScrum = _definition.Kind == BoardKind.Scrum;
        _filter = _definition.Filter;
        _swimlanes = _definition.Swimlanes;
        _hideSubtasks = _definition.HideSubtasks;
        _hideEpics = _definition.HideEpics;
        _hideDone = _definition.HideDoneAfterDays is not null;
        _doneDays = (_definition.HideDoneAfterDays ?? 14).ToString(CultureInfo.CurrentCulture);
        foreach (var (id, name) in CardFieldNames)
            CardFields.Add(new ChecklistItem(id, name, _definition.CardFields.Contains(id)));
        foreach (var q in _definition.QuickFilters)
            QuickFilters.Add(new QuickFilterRow(q.Id, q.Name, q.Query));
        ValidateFilter();
    }

    private static readonly (string Id, string Name)[] CardFieldNames =
    [
        ("epic", "Epic"), ("storyPoints", "Story points"), ("priority", "Priority"), ("assignee", "Assignee"),
        ("labels", "Labels"), ("due", "Due date"), ("remainingEstimate", "Remaining estimate"),
    ];

    public override string Title => "Board Settings";
    public override string OkText => "Save";
    public override double Width => 680;
    public override double Height => 640;

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isScrum;
    [ObservableProperty] private string _filter;
    [ObservableProperty] private string? _filterError;
    [ObservableProperty] private string? _filterCount;
    [ObservableProperty] private SwimlaneMode _swimlanes;
    public IReadOnlyList<SwimlaneMode> SwimlaneModes { get; } = Enum.GetValues<SwimlaneMode>();
    [ObservableProperty] private bool _hideSubtasks;
    [ObservableProperty] private bool _hideEpics;
    [ObservableProperty] private bool _hideDone;
    [ObservableProperty] private string _doneDays;
    [ObservableProperty] private string? _error;

    public ObservableCollection<ChecklistItem> CardFields { get; } = [];
    public ObservableCollection<QuickFilterRow> QuickFilters { get; } = [];

    public string FilterHelp => "Examples: type = bug · component = UI · labels in (web, api) · assignee = currentUser(). Empty shows every issue.";

    partial void OnFilterChanged(string value) => ValidateFilter();

    private void ValidateFilter()
    {
        try
        {
            var query = QueryCompiler.Compile(Filter, _context.QueryContext());
            FilterError = null;
            var count = _context.Snapshot.Issues.Values.Count(query.Matches);
            FilterCount = count == 1 ? "1 issue matches" : $"{count} issues match";
        }
        catch (QueryException e)
        {
            FilterError = e.Message;
            FilterCount = null;
        }
    }

    [RelayCommand]
    private void AddQuickFilter() => QuickFilters.Add(new QuickFilterRow(null, "New filter", "assignee = currentUser()"));

    [RelayCommand]
    private void RemoveQuickFilter(QuickFilterRow? row)
    {
        if (row is not null)
            QuickFilters.Remove(row);
    }

    protected override async Task<bool> ApplyChangesAsync()
    {
        Error = null;
        if (FilterError is not null)
        {
            Error = "Fix the filter first.";
            return false;
        }
        int? days = null;
        if (HideDone)
        {
            if (!int.TryParse(DoneDays.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out var d))
            {
                Error = "Days must be a whole number.";
                return false;
            }
            days = d;
        }
        foreach (var row in QuickFilters)
        {
            try
            {
                QueryCompiler.Compile(row.Query, _context.QueryContext());
                row.Error = null;
            }
            catch (QueryException e)
            {
                row.Error = e.Message;
                Error = $"The quick filter \"{row.Name}\" has a problem.";
                return false;
            }
        }

        var ok = await _context.RunAsync(new EditBoardView(_definition.Id)
        {
            Name = Name,
            Kind = IsScrum ? BoardKind.Scrum : BoardKind.Kanban,
            Filter = Filter,
            Swimlanes = Swimlanes,
            HideSubtasks = HideSubtasks,
            HideEpics = HideEpics,
            HideDoneAfterDays = days,
            KeepDoneForever = !HideDone,
            CardFields = CardFields.Where(c => c.IsChecked).Select(c => c.Id).ToList(),
        });
        if (!ok)
            return false;

        // Quick filters: removed ones, changed ones, new ones.
        var kept = QuickFilters.Select(r => r.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var removed in _definition.QuickFilters.Where(q => !kept.Contains(q.Id)))
            await _context.RunAsync(new SetQuickFilter(_definition.Id, removed.Id, removed.Name, null));
        foreach (var row in QuickFilters)
        {
            var old = _definition.QuickFilters.FirstOrDefault(q => q.Id == row.Id);
            if (old is not null && old.Name == row.Name.Trim() && old.Query == row.Query.Trim())
                continue;
            await _context.RunAsync(new SetQuickFilter(_definition.Id, row.Id, row.Name, row.Query));
        }
        return true;
    }
}
