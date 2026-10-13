using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Search;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Board;

/// <summary>The Search page: a query, its results as a list or a board, saved filters.</summary>
public sealed class SearchPageProvider : IBoardPageProvider
{
    public const string PageId = "search";
    public string Id => PageId;
    public string Title => "Search";
    public int Order => 900;
    public BoardPageViewModel Create(BoardPageHost host) => new SearchPageViewModel(host);
}

/// <summary>A row of search results.</summary>
public sealed record SearchRow(IssueItemViewModel Item, string Assignee, string Updated)
{
    public override string ToString() => Item.ToString();
}

/// <summary>A column of search results shown as a board.</summary>
public sealed record SearchColumn(string Name, IReadOnlyList<IssueItemViewModel> Cards)
{
    public string Header => $"{Name} ({Cards.Count})";
}

public sealed partial class SearchPageViewModel : BoardPageViewModel, ICardHost
{
    public SearchPageViewModel(BoardPageHost host) : base(host)
    {
        _query = "statusCategory != Done ORDER BY updated DESC";
        LoadFilters();
        Run();
    }

    public override string Title => "Search";

    System.Windows.Input.ICommand ICardHost.SelectCommand => SelectCommand;
    System.Windows.Input.ICommand ICardHost.OpenCommand => OpenCommand;

    [ObservableProperty] private string _query;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _asBoard;
    public ObservableCollection<SearchRow> Results { get; } = [];
    public ObservableCollection<SearchColumn> Columns { get; } = [];

    public ObservableCollection<SavedFilter> Filters { get; } = [];
    [ObservableProperty] private SavedFilter? _selectedFilter;

    public string Help =>
        "Fields: type, status, statusCategory, priority, assignee, reporter, labels, component, fixVersion, sprint, epic, parent, " +
        "summary, description, comment, text, points, originalEstimate, remainingEstimate, created, updated, resolved, due, and custom fields by name.\n" +
        "Operators: = != > >= < <= ~ (contains) !~ IN (…) NOT IN (…) IS EMPTY IS NOT EMPTY, joined with AND / OR / NOT and ( ).\n" +
        "Values: names or \"quoted names\"; currentUser(), openSprints(), startOfWeek(), -1d, -2w, 2026-10-31. Then ORDER BY a field [DESC].";

    public IReadOnlyList<string> Examples { get; } =
    [
        "assignee = currentUser() AND statusCategory != Done",
        "type = bug AND priority >= High ORDER BY priority DESC",
        "created >= -7d ORDER BY created DESC",
        "sprint in openSprints() AND points IS EMPTY",
        "summary ~ \"login\" OR description ~ \"login\"",
        "labels in (ui, design) AND resolution IS EMPTY",
    ];

    public override void OnBoardChanged()
    {
        LoadFilters();
        Run();
    }

    partial void OnAsBoardChanged(bool value) => Run();

    partial void OnSelectedFilterChanged(SavedFilter? value)
    {
        if (value is null)
            return;
        Query = value.Query;
        Run();
    }

    [RelayCommand]
    private void UseExample(string? example)
    {
        if (example is null)
            return;
        Query = example;
        Run();
    }

    [RelayCommand]
    public void Run()
    {
        var board = Context.Snapshot;
        Results.Clear();
        Columns.Clear();
        CompiledQuery compiled;
        try
        {
            compiled = QueryCompiler.Compile(Query, Context.QueryContext());
            Error = null;
        }
        catch (QueryException e)
        {
            var query = Query ?? "";
            Error = e.Position < query.Length ? $"{e.Message} (at \"{query[e.Position..Math.Min(query.Length, e.Position + 20)]}\")" : e.Message;
            ResultText = "";
            return;
        }
        var results = compiled.Run(board.Issues.Values);
        ResultText = results.Count == 1 ? "1 issue" : $"{results.Count} issues";
        if (!AsBoard)
        {
            foreach (var issue in results.Take(1000))
                Results.Add(new SearchRow(new IssueItemViewModel(issue, board, Context) { Host = this }, Context.PersonName(issue.Assignee),
                    IssueDetailViewModel.RelativeOrDate(issue.Updated)));
            if (results.Count > 1000)
                ResultText += " (first 1000 shown)";
            return;
        }
        // As a board: the shown board's columns, results in them; statuses on no column go last.
        var definition = Host.Definition;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in definition.Columns)
        {
            var cards = results.Where(i => column.Statuses.Contains(i.Status)).Select(i => new IssueItemViewModel(i, board, Context, definition.CardFields) { Host = this }).ToList();
            used.UnionWith(cards.Select(c => c.Id));
            Columns.Add(new SearchColumn(column.Name, cards));
        }
        var rest = results.Where(i => !used.Contains(i.Id)).Select(i => new IssueItemViewModel(i, board, Context, definition.CardFields) { Host = this }).ToList();
        if (rest.Count > 0)
            Columns.Add(new SearchColumn("Not on this board", rest));
    }

    [RelayCommand]
    private void Select(IssueItemViewModel? item) => Host.SelectIssue(item?.Id);

    [RelayCommand]
    private void SelectRow(SearchRow? row) => Host.SelectIssue(row?.Item.Id);

    [RelayCommand]
    private void Open(IssueItemViewModel? item)
    {
        if (item is not null)
            Host.OpenIssue(item.Id);
    }

    [RelayCommand]
    private void OpenRow(SearchRow? row)
    {
        if (row is not null)
            Host.OpenIssue(row.Item.Id);
    }

    [RelayCommand]
    private async Task SaveFilterAsync()
    {
        var dialog = new TextInputDialogViewModel("Save Filter", "Name", SelectedFilter?.Name ?? "", "Save")
        {
            Explanation = "Saved filters are part of the board: everyone using it sees them.",
        };
        if (!await Context.Dialogs.ShowDialogAsync(dialog))
            return;
        var existing = Filters.FirstOrDefault(f => string.Equals(f.Name, dialog.Text, StringComparison.OrdinalIgnoreCase));
        await Context.RunAsync(new SaveFilter(dialog.Text, Query) { Id = existing?.Id });
    }

    [RelayCommand]
    private async Task DeleteFilterAsync()
    {
        if (SelectedFilter is not { } filter)
            return;
        if (await Context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Delete Filter", $"Delete the saved filter \"{filter.Name}\"?", "Delete", [filter.Name])))
            await Context.RunAsync(new DeleteFilter(filter.Id));
    }

    [RelayCommand]
    private async Task BulkEditResultsAsync()
    {
        var ids = Results.Select(r => r.Item.Id).Concat(Columns.SelectMany(c => c.Cards).Select(c => c.Id)).Distinct().ToList();
        if (ids.Count > 0)
            await Context.Dialogs.ShowDialogAsync(new BulkEditDialogViewModel(Context, ids));
    }

    private void LoadFilters()
    {
        var selected = SelectedFilter?.Id;
        Filters.Clear();
        foreach (var f in Context.Snapshot.Filters.Values.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            Filters.Add(f);
#pragma warning disable MVVMTK0034 // set without running OnSelectedFilterChanged (that would replace the query)
        _selectedFilter = Filters.FirstOrDefault(f => f.Id == selected);
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(SelectedFilter));
    }
}
