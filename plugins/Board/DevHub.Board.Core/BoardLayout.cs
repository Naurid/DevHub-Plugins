using DevHub.Board.Core.Model;
using DevHub.Board.Core.Search;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Core;

/// <summary>What the board screen asks for besides the board definition: active quick filters and a text search.</summary>
public sealed record BoardViewOptions
{
    public IReadOnlySet<string> QuickFilters { get; init; } = new HashSet<string>();

    /// <summary>Words that must appear in the key or summary (the board's search box).</summary>
    public string Search { get; init; } = "";

    /// <summary>Only these assignees (the avatar filter row); empty = everyone.</summary>
    public IReadOnlySet<string> Assignees { get; init; } = new HashSet<string>();
}

/// <summary>A column as shown: its definition, its cards in rank order (all lanes), and its WIP state.</summary>
public sealed record ColumnLayout(BoardColumn Column, IReadOnlyList<Issue> Cards)
{
    public bool OverLimit => Column.WipLimit is { } max && Cards.Count > max;
    public bool UnderMinimum => Column.WipMinimum is { } min && Cards.Count < min;

    /// <summary>The column's statuses' category (the last status decides; Done columns get a check).</summary>
    public StatusCategory? Category { get; init; }
}

/// <summary>A swimlane: a title, and for each column (same order as <see cref="BoardLayoutResult.Columns"/>) its cards.</summary>
public sealed record SwimlaneLayout(string Key, string Title, IReadOnlyList<IReadOnlyList<Issue>> Cells)
{
    public int Count => Cells.Sum(c => c.Count);
}

/// <summary>Everything the board screen draws.</summary>
public sealed record BoardLayoutResult(
    IReadOnlyList<ColumnLayout> Columns,
    IReadOnlyList<SwimlaneLayout> Swimlanes,
    IReadOnlyList<(Status Status, int Count)> NotOnBoard,
    string? Problem)
{
    public int CardCount => Columns.Sum(c => c.Cards.Count);

    /// <summary>For a Scrum board: the active sprints shown (empty = none started yet).</summary>
    public IReadOnlyList<Sprint> ActiveSprints { get; init; } = [];
}

/// <summary>
/// Works out a board's columns, cards and swimlanes from a snapshot (pure: the screen only draws the result).
/// Which issues show: the board's filter, the active quick filters, the search, Scrum boards only the active
/// sprint's issues, epics / sub-tasks if the board shows them, and Done issues only for a while on Kanban boards.
/// </summary>
public static class BoardLayout
{
    public static BoardLayoutResult Build(BoardSnapshot board, BoardDefinition definition, BoardViewOptions options, QueryContext context)
    {
        string? problem = null;
        Func<Issue, bool> filter = _ => true;
        try
        {
            var filters = new List<Func<Issue, bool>> { QueryCompiler.Compile(definition.Filter, context).Matches };
            filters.AddRange(definition.QuickFilters.Where(q => options.QuickFilters.Contains(q.Id))
                .Select(q => QueryCompiler.Compile(q.Query, context).Matches));
            filter = i => filters.All(f => f(i));
        }
        catch (QueryException e)
        {
            problem = L.T("The board's filter has a problem: ") + e.Message;
        }

        var words = options.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var activeSprints = board.Sprints.Values.Where(s => s.State == SprintState.Active).OrderBy(s => s.Sequence).ToList();
        var activeIds = activeSprints.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var doneCutoff = definition.HideDoneAfterDays is { } days && definition.Kind == BoardKind.Kanban
            ? context.Now.AddDays(-days)
            : (DateTimeOffset?)null;

        bool Shows(Issue issue)
        {
            var level = board.LevelOf(issue);
            if (level == IssueTypeLevel.Epic && definition.HideEpics)
                return false;
            if (level == IssueTypeLevel.Subtask && definition.HideSubtasks)
                return false;
            if (definition.Kind == BoardKind.Scrum && (issue.Sprint is null || !activeIds.Contains(issue.Sprint)))
                return false;
            if (doneCutoff is { } cutoff && board.IsDone(issue) && issue.Resolved is { } resolved && resolved < cutoff)
                return false;
            if (options.Assignees.Count > 0 && (issue.Assignee is null || !options.Assignees.Contains(issue.Assignee)))
                return false;
            if (words.Length > 0 && !words.All(w => issue.Summary.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                                                    || issue.Key.Contains(w, StringComparison.OrdinalIgnoreCase)))
                return false;
            return filter(issue);
        }

        var shown = board.Issues.Values.Where(Shows).OrderBy(i => i.Rank, StringComparer.Ordinal)
            .ThenBy(i => IssueKey.TryParse(i.Key, out _, out var n) ? n : 0).ToList();

        var columns = definition.Columns.Select(c => new ColumnLayout(c, shown.Where(i => c.Statuses.Contains(i.Status)).ToList())
        {
            Category = c.Statuses.Count == 0 ? null : board.Project.Status(c.Statuses[^1])?.Category,
        }).ToList();

        var onBoard = definition.Columns.SelectMany(c => c.Statuses).ToHashSet(StringComparer.Ordinal);
        var notOnBoard = shown.Where(i => !onBoard.Contains(i.Status)).GroupBy(i => i.Status)
            .Select(g => (Status: board.Project.Status(g.Key) ?? new Status { Id = g.Key, Name = g.Key }, Count: g.Count()))
            .OrderBy(s => s.Status.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var lanes = Lanes(board, definition, columns);
        return new BoardLayoutResult(columns, lanes, notOnBoard, problem) { ActiveSprints = activeSprints };
    }

    private static IReadOnlyList<SwimlaneLayout> Lanes(BoardSnapshot board, BoardDefinition definition, List<ColumnLayout> columns)
    {
        if (definition.Swimlanes == SwimlaneMode.None)
            return [new SwimlaneLayout("", "", columns.Select(c => c.Cards).ToList())];

        var project = board.Project;
        // Lane key and title per issue, plus the order lanes come in (the "none" lane last).
        (string Key, string Title, string Sort) LaneOf(Issue issue) => definition.Swimlanes switch
        {
            SwimlaneMode.Assignee => project.Person(issue.Assignee) is { } p ? (p.Id, p.Name, "0" + p.Name) : ("", L.T("Unassigned"), "1"),
            SwimlaneMode.Epic => board.EpicOf(issue) is { } e ? (e.Id, $"{e.Key} {e.Summary}", "0" + e.Rank) : ("", L.T("No epic"), "1"),
            SwimlaneMode.Priority => project.Priority(issue.Priority) is { } pr
                ? (pr.Id, pr.Name, "0" + project.Priorities.ToList().IndexOf(pr).ToString("D4"))
                : ("", L.T("No priority"), "1"),
            SwimlaneMode.Type => project.IssueType(issue.Type) is { } t
                ? (t.Id, t.Name, "0" + project.IssueTypes.ToList().IndexOf(t).ToString("D4"))
                : ("", L.T("Other"), "1"),
            _ => ("", "", ""),
        };

        var all = columns.SelectMany(c => c.Cards).Select(i => (Issue: i, Lane: LaneOf(i))).ToList();
        return all.Select(x => x.Lane).DistinctBy(l => l.Key).OrderBy(l => l.Sort, StringComparer.CurrentCultureIgnoreCase)
            .Select(lane => new SwimlaneLayout(lane.Key, lane.Title,
                columns.Select(c => (IReadOnlyList<Issue>)c.Cards.Where(i => LaneOf(i).Key == lane.Key).ToList()).ToList()))
            .ToList();
    }
}
