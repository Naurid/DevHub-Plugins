using DevHub.Board.Core.Model;
using DevHub.Board.Core.Search;
using DevHub.Board.Core.Storage;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Core;

/// <summary>Helpers for operations on one board definition.</summary>
internal static class BoardViews
{
    public static BoardDefinition Require(BoardSnapshot board, string boardId) =>
        board.Boards.GetValueOrDefault(boardId) ?? throw new BoardRuleException(L.T("That board doesn't exist any more."));

    public static BoardColumn RequireColumn(BoardDefinition definition, string columnId) =>
        definition.Columns.FirstOrDefault(c => c.Id == columnId) ?? throw new BoardRuleException(L.T("That column doesn't exist any more."));

    public static void Write(ChangeBuilder change, BoardDefinition definition) => change.Write(BoardPaths.Board(definition.Id), definition);

    public static string NewColumnId(ChangeBuilder change, BoardDefinition definition) =>
        Definitions.NewIdFor(change, null, definition.Columns.Select(c => c.Id));

    /// <summary>Checks a query can be read and run on this board (filters, quick filters).</summary>
    public static void CheckQuery(BoardSnapshot board, string query)
    {
        try
        {
            QueryCompiler.Compile(query, new QueryContext(board, null, DateTimeOffset.UnixEpoch));
        }
        catch (QueryException e)
        {
            throw new BoardRuleException(L.T("The query has a problem: ") + e.Message);
        }
    }
}

/// <summary>
/// Creates another board (view) on the project: its columns are the default workflow's statuses, one each
/// (<see cref="Empty"/>: no columns).
/// </summary>
public sealed record CreateBoardView(string Name, BoardKind Kind) : IBoardOperation
{
    public string? Id { get; init; }
    public bool Empty { get; init; }
    public string Filter { get; init; } = "";

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var name = Definitions.RequireName(Name, "board");
        Definitions.RequireUnique(board.Boards.Values, b => b.Name, name, "board");
        BoardViews.CheckQuery(board, Filter);
        var change = new ChangeBuilder(board, context);
        var statuses = Empty ? [] : board.DefaultWorkflow?.Statuses ?? [];
        var columns = new List<BoardColumn>();
        foreach (var statusId in statuses)
        {
            var status = board.Project.Status(statusId);
            if (status is null)
                continue;
            columns.Add(new BoardColumn { Id = Definitions.NewIdFor(change, null, columns.Select(c => c.Id)), Name = status.Name, Statuses = [status.Id] });
        }
        var definition = new BoardDefinition
        {
            Id = Definitions.NewIdFor(change, Id, board.Boards.Keys),
            Name = name,
            Kind = Kind,
            Columns = columns,
            Filter = Filter.Trim(),
            QuickFilters =
            [
                new QuickFilter { Id = change.NewId(), Name = L.T("Only my issues"), Query = "assignee = currentUser()" },
                new QuickFilter { Id = change.NewId(), Name = L.T("Recently updated"), Query = "updated >= -1d" },
            ],
        };
        BoardViews.Write(change, definition);
        return change.Build(L.T($"Board {name} created"));
    }
}

/// <summary>Changes a board's name, kind, filter and display options (what <see cref="BoardDefinition"/> holds besides columns).</summary>
public sealed record EditBoardView(string BoardId) : IBoardOperation
{
    public string? Name { get; init; }
    public BoardKind? Kind { get; init; }
    public string? Filter { get; init; }
    public SwimlaneMode? Swimlanes { get; init; }
    public bool? HideSubtasks { get; init; }
    public bool? HideEpics { get; init; }
    public int? HideDoneAfterDays { get; init; }
    public bool KeepDoneForever { get; init; }
    public IReadOnlyList<string>? CardFields { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var definition = BoardViews.Require(board, BoardId);
        var name = Name is null ? definition.Name : Definitions.RequireName(Name, "board");
        if (Name is not null)
            Definitions.RequireUnique(board.Boards.Values, b => b.Name, name, "board", b => b.Id == BoardId);
        if (Filter is not null)
            BoardViews.CheckQuery(board, Filter);
        if (HideDoneAfterDays is < 0)
            throw new BoardRuleException(L.T("Days can't be negative."));
        var change = new ChangeBuilder(board, context);
        BoardViews.Write(change, definition with
        {
            Name = name,
            Kind = Kind ?? definition.Kind,
            Filter = Filter?.Trim() ?? definition.Filter,
            Swimlanes = Swimlanes ?? definition.Swimlanes,
            HideSubtasks = HideSubtasks ?? definition.HideSubtasks,
            HideEpics = HideEpics ?? definition.HideEpics,
            HideDoneAfterDays = KeepDoneForever ? null : HideDoneAfterDays ?? definition.HideDoneAfterDays,
            CardFields = CardFields ?? definition.CardFields,
        });
        return change.Build(L.T($"Board {name} changed"));
    }
}

/// <summary>Deletes a board (view); the issues stay. The last board can't go.</summary>
public sealed record DeleteBoardView(string BoardId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var definition = BoardViews.Require(board, BoardId);
        if (board.Boards.Count == 1)
            throw new BoardRuleException(L.T("A project needs at least one board."));
        var change = new ChangeBuilder(board, context);
        change.Delete(BoardPaths.Board(definition.Id));
        return change.Build(L.T($"Board {definition.Name} deleted"));
    }
}

/// <summary>
/// Adds a column right on the board. It creates a status of the same name (in every workflow) unless one exists
/// that's on no column of this board yet, which it takes instead.
/// </summary>
public sealed record AddColumn(string BoardId, string Name, StatusCategory Category = StatusCategory.InProgress) : IBoardOperation
{
    /// <summary>Position among the columns; null = last.</summary>
    public int? Index { get; init; }
    public string? ColumnId { get; init; }
    public string? StatusId { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var definition = BoardViews.Require(board, BoardId);
        var name = Definitions.RequireName(Name, "column");
        Definitions.RequireUnique(definition.Columns, c => c.Name, name, "column");
        var change = new ChangeBuilder(board, context);

        var existing = board.Project.Statuses.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        string statusId;
        if (existing is not null)
        {
            if (definition.ColumnOf(existing.Id) is { } holder)
                throw new BoardRuleException(L.T($"The status {existing.Name} is already in the column {holder.Name}."));
            statusId = existing.Id;
        }
        else
        {
            statusId = AddStatus.AddTo(change, name, Category, StatusId, null).Id;
        }

        var column = new BoardColumn
        {
            Id = ColumnId is not null ? Definitions.NewIdFor(change, ColumnId, definition.Columns.Select(c => c.Id)) : BoardViews.NewColumnId(change, definition),
            Name = name,
            Statuses = [statusId],
        };
        var columns = definition.Columns.ToList();
        columns.Insert(Math.Clamp(Index ?? columns.Count, 0, columns.Count), column);
        BoardViews.Write(change, definition with { Columns = columns });
        return change.Build(L.T($"Board {definition.Name}: column {name} added"));
    }
}

/// <summary>Renames a column; its status too when it holds just one that had the column's old name.</summary>
public sealed record RenameColumn(string BoardId, string ColumnId, string Name) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var definition = BoardViews.Require(board, BoardId);
        var column = BoardViews.RequireColumn(definition, ColumnId);
        var name = Definitions.RequireName(Name, "column");
        Definitions.RequireUnique(definition.Columns, c => c.Name, name, "column", c => c.Id == ColumnId);
        var change = new ChangeBuilder(board, context);
        BoardViews.Write(change, definition with
        {
            Columns = Definitions.Replace(definition.Columns, c => c.Id == ColumnId, c => c with { Name = name }),
        });
        if (column.Statuses.Count == 1 && board.Project.Status(column.Statuses[0]) is { } status
            && string.Equals(status.Name, column.Name, StringComparison.OrdinalIgnoreCase)
            && !board.Project.Statuses.Any(s => s.Id != status.Id && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            change.WriteProject(change.Project with
            {
                Statuses = Definitions.Replace(change.Project.Statuses, s => s.Id == status.Id, s => s with { Name = name }),
            });
        }
        return change.Build(L.T($"Board {definition.Name}: column {column.Name} renamed to {name}"));
    }
}

/// <summary>Moves a column along the board.</summary>
public sealed record MoveColumn(string BoardId, string ColumnId, int Index) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var definition = BoardViews.Require(board, BoardId);
        var change = new ChangeBuilder(board, context);
        BoardViews.Write(change, definition with { Columns = Definitions.Move(definition.Columns, c => c.Id == ColumnId, Index) });
        return change.Build(L.T($"Board {definition.Name}: columns reordered"));
    }
}

/// <summary>Merges a column into another: its statuses join the other column and it disappears.</summary>
public sealed record MergeColumns(string BoardId, string ColumnId, string IntoColumnId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var definition = BoardViews.Require(board, BoardId);
        var column = BoardViews.RequireColumn(definition, ColumnId);
        var into = BoardViews.RequireColumn(definition, IntoColumnId);
        if (column.Id == into.Id)
            throw new BoardRuleException(L.T("A column can't be merged into itself."));
        var change = new ChangeBuilder(board, context);
        BoardViews.Write(change, definition with
        {
            Columns = definition.Columns.Where(c => c.Id != ColumnId)
                .Select(c => c.Id == IntoColumnId ? c with { Statuses = [.. c.Statuses, .. column.Statuses] } : c).ToList(),
        });
        return change.Build(L.T($"Board {definition.Name}: {column.Name} merged into {into.Name}"));
    }
}

/// <summary>
/// Deletes a column. Its statuses either stay (listed as "not on this board") or, with
/// <see cref="DeleteStatuses"/>, are deleted with their issues moved to <see cref="MoveIssuesTo"/>.
/// </summary>
public sealed record DeleteColumn(string BoardId, string ColumnId) : IBoardOperation
{
    public bool DeleteStatuses { get; init; }
    public string? MoveIssuesTo { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var definition = BoardViews.Require(board, BoardId);
        var column = BoardViews.RequireColumn(definition, ColumnId);
        if (!DeleteStatuses)
        {
            var change = new ChangeBuilder(board, context);
            BoardViews.Write(change, definition with { Columns = definition.Columns.Where(c => c.Id != ColumnId).ToList() });
            return change.Build(L.T($"Board {definition.Name}: column {column.Name} deleted"));
        }

        if (MoveIssuesTo is not null && column.Statuses.Contains(MoveIssuesTo))
            throw new BoardRuleException(L.T("The issues can't move to a status that's being deleted."));
        // Delete each status in turn (one combined change), then the column.
        var current = board;
        var files = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var statusId in column.Statuses)
        {
            var step = new DeleteStatus(statusId, MoveIssuesTo).Apply(current, context);
            foreach (var (path, bytes) in step.Files)
                files[path] = bytes;
            current = current.Apply(step, current.Version);
        }
        var final = new ChangeBuilder(current, context);
        var after = current.Boards[BoardId];
        BoardViews.Write(final, after with { Columns = after.Columns.Where(c => c.Id != ColumnId).ToList() });
        foreach (var (path, bytes) in final.Build("").Files)
            files[path] = bytes;
        return new BoardChange(L.T($"Board {definition.Name}: column {column.Name} and its statuses deleted"), files);
    }
}

/// <summary>Sets which statuses a column holds; a status leaves any other column of this board it was in.</summary>
public sealed record SetColumnStatuses(string BoardId, string ColumnId, IReadOnlyList<string> Statuses) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var definition = BoardViews.Require(board, BoardId);
        var column = BoardViews.RequireColumn(definition, ColumnId);
        foreach (var id in Statuses.Where(id => board.Project.Status(id) is null))
            throw new BoardRuleException(L.T($"There's no status \"{id}\"."));
        var wanted = Statuses.Distinct().ToList();
        var change = new ChangeBuilder(board, context);
        BoardViews.Write(change, definition with
        {
            Columns = definition.Columns.Select(c => c.Id == ColumnId
                ? c with { Statuses = wanted }
                : c with { Statuses = c.Statuses.Where(s => !wanted.Contains(s)).ToList() }).ToList(),
        });
        return change.Build(L.T($"Board {definition.Name}: statuses of {column.Name} changed"));
    }
}

/// <summary>Sets a column's WIP limits (null = none).</summary>
public sealed record SetWipLimits(string BoardId, string ColumnId, int? Minimum, int? Maximum) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var definition = BoardViews.Require(board, BoardId);
        var column = BoardViews.RequireColumn(definition, ColumnId);
        if (Minimum is < 0 || Maximum is < 1 || (Minimum is { } min && Maximum is { } max && min > max))
            throw new BoardRuleException(L.T("The maximum must be at least 1 and not below the minimum."));
        var change = new ChangeBuilder(board, context);
        BoardViews.Write(change, definition with
        {
            Columns = Definitions.Replace(definition.Columns, c => c.Id == ColumnId, c => c with { WipMinimum = Minimum, WipLimit = Maximum }),
        });
        return change.Build(L.T($"Board {definition.Name}: WIP limits of {column.Name} changed"));
    }
}

/// <summary>Adds, changes (same id) or removes (null query) a quick filter.</summary>
public sealed record SetQuickFilter(string BoardId, string? QuickFilterId, string Name, string? Query) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var definition = BoardViews.Require(board, BoardId);
        var change = new ChangeBuilder(board, context);
        if (Query is null)
        {
            var removed = definition.QuickFilters.FirstOrDefault(q => q.Id == QuickFilterId) ?? throw new BoardRuleException(L.T("That quick filter doesn't exist any more."));
            BoardViews.Write(change, definition with { QuickFilters = definition.QuickFilters.Where(q => q.Id != QuickFilterId).ToList() });
            return change.Build(L.T($"Board {definition.Name}: quick filter {removed.Name} removed"));
        }
        var name = Definitions.RequireName(Name, L.T("quick filter"));
        Definitions.RequireUnique(definition.QuickFilters, q => q.Name, name, L.T("quick filter"), q => q.Id == QuickFilterId);
        BoardViews.CheckQuery(board, Query);
        if (QuickFilterId is not null && definition.QuickFilters.All(q => q.Id != QuickFilterId))
            throw new BoardRuleException(L.T("That quick filter doesn't exist any more."));
        var filter = new QuickFilter
        {
            Id = QuickFilterId ?? Definitions.NewIdFor(change, null, definition.QuickFilters.Select(q => q.Id)),
            Name = name,
            Query = Query.Trim(),
        };
        BoardViews.Write(change, definition with
        {
            QuickFilters = QuickFilterId is null
                ? [.. definition.QuickFilters, filter]
                : Definitions.Replace(definition.QuickFilters, q => q.Id == QuickFilterId, _ => filter),
        });
        return change.Build(L.T($"Board {definition.Name}: quick filter {name} saved"));
    }
}

/// <summary>
/// Drops a card on a board: into a column (moving the issue to the first of the column's statuses its workflow
/// allows) and between two cards (ranking it there). Either part may be a no-op.
/// </summary>
public sealed record MoveCard(string BoardId, string IssueId, string? ToColumnId, string? Above, string? Below) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var definition = BoardViews.Require(board, BoardId);
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException(L.T("The issue doesn't exist any more."));
        var files = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        var messages = new List<string>();
        var current = board;

        if (ToColumnId is not null)
        {
            var column = BoardViews.RequireColumn(definition, ToColumnId);
            if (!column.Statuses.Contains(issue.Status))
            {
                var target = TargetStatus(board, issue, column)
                             ?? throw new BoardRuleException(column.Statuses.Count == 0
                                 ? L.T($"The column {column.Name} has no status yet: give it one first.")
                                 : L.T($"{issue.Key} can't move to {column.Name}: its workflow doesn't allow it."));
                var step = new TransitionIssue(issue.Id, target).Apply(current, context);
                Merge(step);
            }
        }
        if (Above is not null || Below is not null)
        {
            var step = new RankIssue(issue.Id, Above, Below).Apply(current, context);
            Merge(step);
        }
        return new BoardChange(messages.Count == 0 ? L.T("No change") : string.Join("; ", messages), files);

        void Merge(BoardChange step)
        {
            foreach (var (path, bytes) in step.Files)
                files[path] = bytes;
            messages.Add(step.Message);
            current = current.Apply(step, current.Version);
        }
    }

    /// <summary>The status an issue would get in a column: the first of its statuses the workflow allows, or null.</summary>
    public static string? TargetStatus(BoardSnapshot board, Issue issue, BoardColumn column)
    {
        var workflow = board.WorkflowFor(issue.Type);
        return column.Statuses.FirstOrDefault(s => workflow is not null && workflow.Allows(issue.Status, s));
    }
}
