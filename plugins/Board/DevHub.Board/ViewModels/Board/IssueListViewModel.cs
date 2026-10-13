using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Services.Board;

namespace DevHub.ViewModels.Board;

/// <summary>
/// A ranked list of issues (the backlog, a sprint's issues): rows in rank order, a multi-selection, point totals.
/// Pages own the lists and decide what a drop into one means (<see cref="IssueListDrop"/>), so Board's backlog and
/// Board.Scrum's sprint planning share it.
/// </summary>
public sealed partial class IssueListViewModel(string id, string title) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] private string _title = title;
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private string? _emptyText = "Nothing here.";

    /// <summary>Anything the page wants to keep with the list (Board.Scrum: the sprint).</summary>
    public object? Tag { get; set; }

    /// <summary>Buttons in the list's header (Board.Scrum: Start Sprint, Complete Sprint, Edit, Delete).</summary>
    public ObservableCollection<ListAction> Actions { get; } = [];

    public ObservableCollection<IssueItemViewModel> Items { get; } = [];
    public ObservableCollection<IssueItemViewModel> Selected { get; } = [];
    public bool IsEmpty => Items.Count == 0;

    /// <summary>Fills the list with <paramref name="issues"/> (already filtered), by rank; keeps the selection by id.</summary>
    public void Fill(IEnumerable<Issue> issues, BoardSnapshot board, BoardContext context, IReadOnlyList<string>? cardFields = null, ICardHost? host = null)
    {
        var selected = Selected.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        Items.Clear();
        foreach (var issue in issues.OrderBy(i => i.Rank, StringComparer.Ordinal).ThenBy(i => i.Key, StringComparer.Ordinal))
            Items.Add(new IssueItemViewModel(issue, board, context, cardFields) { Host = host });
        Selected.Clear();
        foreach (var item in Items.Where(i => selected.Contains(i.Id)))
            Selected.Add(item);
        var points = Items.Sum(i => i.Issue.StoryPoints ?? 0);
        var estimated = Items.Count(i => i.Issue.StoryPoints is not null);
        Summary = Items.Count == 0 ? "no issues"
            : $"{Items.Count} issue{(Items.Count == 1 ? "" : "s")} · {points.ToString("0.##", CultureInfo.CurrentCulture)} points"
              + (estimated < Items.Count ? $" · {Items.Count - estimated} not estimated" : "");
        OnPropertyChanged(nameof(IsEmpty));
    }

    public override string ToString() => Title;
}

/// <summary>Where a dragged issue lands: a list and the issues directly above and below it there.</summary>
public sealed record IssueListDrop(IssueListViewModel Source, IssueListViewModel Target, IssueItemViewModel Item, string? Above, string? Below);

/// <summary>Turns an <see cref="Controls.ItemsDrop"/> on issue lists into an <see cref="IssueListDrop"/>.</summary>
public static class IssueListDrops
{
    public static IssueListDrop? From(Controls.ItemsDrop drop)
    {
        if (drop is not { Item: IssueItemViewModel item, TargetList: IssueListViewModel target })
            return null;
        var source = drop.SourceList as IssueListViewModel ?? target;
        var others = target.Items.Where(i => i.Id != item.Id).ToList();
        var index = drop.Index;
        if (ReferenceEquals(source, target) && target.Items.IndexOf(item) is var from && from >= 0 && from < index)
            index--;
        index = Math.Clamp(index, 0, others.Count);
        return new IssueListDrop(source, target, item, index > 0 ? others[index - 1].Id : null, index < others.Count ? others[index].Id : null);
    }
}

/// <summary>A button in a list's or a page's header.</summary>
public sealed record ListAction(string Text, System.Windows.Input.ICommand Command)
{
    public string? ToolTip { get; init; }
    public bool IsAccent { get; init; }
    public override string ToString() => Text;
}
