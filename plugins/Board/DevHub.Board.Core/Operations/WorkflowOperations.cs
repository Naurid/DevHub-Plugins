using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Core;

/// <summary>Helpers the definition operations share.</summary>
internal static class Definitions
{
    public static string RequireName(string? name, string what)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BoardRuleException(L.T($"The {what} needs a name."));
        var trimmed = name.Trim();
        if (trimmed.Length > 80)
            throw new BoardRuleException(L.T($"The {what}'s name is longer than 80 characters."));
        return trimmed;
    }

    public static void RequireUnique<T>(IEnumerable<T> items, Func<T, string> name, string candidate, string what, Func<T, bool>? except = null)
    {
        if (items.Any(i => (except is null || !except(i)) && string.Equals(name(i), candidate, StringComparison.OrdinalIgnoreCase)))
            throw new BoardRuleException(L.T($"There's already a {what} called \"{candidate}\"."));
    }

    public static string NewIdFor(ChangeBuilder change, string? wanted, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.Ordinal);
        if (wanted is not null)
        {
            if (!BoardIds.IsValid(wanted) || used.Contains(wanted))
                throw new BoardRuleException(L.T($"The id \"{wanted}\" can't be used."));
            return wanted;
        }
        string id;
        do
            id = change.NewId();
        while (used.Contains(id));
        return id;
    }

    public static IReadOnlyList<T> Replace<T>(IReadOnlyList<T> list, Func<T, bool> match, Func<T, T> change) =>
        list.Select(i => match(i) ? change(i) : i).ToList();

    public static IReadOnlyList<T> Move<T>(IReadOnlyList<T> list, Func<T, bool> match, int index)
    {
        var items = list.ToList();
        var from = items.FindIndex(i => match(i));
        if (from < 0)
            throw new BoardRuleException(L.T("That item doesn't exist any more."));
        var item = items[from];
        items.RemoveAt(from);
        items.Insert(Math.Clamp(index, 0, items.Count), item);
        return items;
    }

    /// <summary>Issue types whose issues follow <paramref name="workflow"/>.</summary>
    public static HashSet<string> TypesUsing(BoardSnapshot board, Workflow workflow) =>
        board.Project.IssueTypes.Where(t => board.WorkflowFor(t.Id)?.Id == workflow.Id).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// Adds a status (to the given workflow, else to every workflow) at the end of the workflow's list. Columns are
/// a board's business: <c>AddColumn</c> creates a status and a column at once.
/// </summary>
public sealed record AddStatus(string Name, StatusCategory Category) : IBoardOperation
{
    public string? Id { get; init; }
    public string? Workflow { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var change = new ChangeBuilder(board, context);
        var status = AddTo(change, Name, Category, Id, Workflow);
        return change.Build(L.T($"Status {status.Name} added"));
    }

    /// <summary>Adds the status inside another operation (AddColumn).</summary>
    internal static Status AddTo(ChangeBuilder change, string name, StatusCategory category, string? id, string? workflowId)
    {
        var board = change.Board;
        var trimmed = Definitions.RequireName(name, "status");
        Definitions.RequireUnique(change.Project.Statuses, s => s.Name, trimmed, "status");
        if (workflowId is not null && !board.Workflows.ContainsKey(workflowId))
            throw new BoardRuleException(L.T("That workflow doesn't exist."));

        var status = new Status
        {
            Id = Definitions.NewIdFor(change, id, change.Project.Statuses.Select(s => s.Id)),
            Name = trimmed,
            Category = category,
        };
        change.WriteProject(change.Project with { Statuses = [.. change.Project.Statuses, status] });
        foreach (var workflow in board.Workflows.Values.Where(w => workflowId is null || w.Id == workflowId))
            change.Write(BoardPaths.Workflow(workflow.Id), workflow with { Statuses = [.. workflow.Statuses, status.Id] });
        return status;
    }
}

/// <summary>Renames a status. Issues refer to it by id, so nothing else changes.</summary>
public sealed record RenameStatus(string StatusId, string Name) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var status = board.Project.Status(StatusId) ?? throw new BoardRuleException(L.T("That status doesn't exist any more."));
        var name = Definitions.RequireName(Name, "status");
        Definitions.RequireUnique(board.Project.Statuses, s => s.Name, name, "status", s => s.Id == StatusId);
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with
        {
            Statuses = Definitions.Replace(board.Project.Statuses, s => s.Id == StatusId, s => s with { Name = name }),
        });
        return change.Build(L.T($"Status {status.Name} renamed to {name}"));
    }
}

/// <summary>
/// Moves a status to another category. Issues in it follow: entering Done gives them a resolution, leaving Done
/// clears it.
/// </summary>
public sealed record SetStatusCategory(string StatusId, StatusCategory Category) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var status = board.Project.Status(StatusId) ?? throw new BoardRuleException(L.T("That status doesn't exist any more."));
        if (status.Category == Category)
            return new BoardChange(L.T("No change"), new Dictionary<string, byte[]?>());
        var change = new ChangeBuilder(board, context);
        var project = board.Project with
        {
            Statuses = Definitions.Replace(board.Project.Statuses, s => s.Id == StatusId, s => s with { Category = Category }),
        };
        change.WriteProject(project);
        var updated = BoardSnapshotView.WithProject(board, project);
        foreach (var issue in board.Issues.Values.Where(i => i.Status == StatusId))
            change.WriteIssue(issue, CreateIssue.WithDoneState(updated, issue, issue.Status, null, context));
        return change.Build(L.T($"Status {status.Name} is now {Category}"));
    }
}

/// <summary>
/// Deletes a status everywhere: issues in it move to <see cref="MoveIssuesTo"/>, it leaves every workflow,
/// transition and board column (a column left without statuses stays, empty).
/// </summary>
public sealed record DeleteStatus(string StatusId, string? MoveIssuesTo) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var status = board.Project.Status(StatusId) ?? throw new BoardRuleException(L.T("That status doesn't exist any more."));
        var affected = board.Issues.Values.Where(i => i.Status == StatusId).ToList();
        if (affected.Count > 0)
        {
            if (MoveIssuesTo is null || MoveIssuesTo == StatusId || board.Project.Status(MoveIssuesTo) is null)
                throw new BoardRuleException(L.T($"{affected.Count} issues are in {status.Name}: choose where they go."));
            foreach (var issue in affected)
            {
                var workflow = board.WorkflowFor(issue.Type);
                if (workflow is not null && !workflow.Statuses.Contains(MoveIssuesTo))
                    throw new BoardRuleException(L.T($"{issue.Key} can't move there: its workflow ({workflow.Name}) doesn't have that status."));
            }
        }
        foreach (var workflow in board.Workflows.Values)
        {
            if (workflow.Statuses.Count == 1 && workflow.Statuses[0] == StatusId)
                throw new BoardRuleException(L.T($"{status.Name} is the only status of the {workflow.Name} workflow."));
        }

        var change = new ChangeBuilder(board, context);
        var project = board.Project with { Statuses = board.Project.Statuses.Where(s => s.Id != StatusId).ToList() };
        change.WriteProject(project);
        var updated = BoardSnapshotView.WithProject(board, project);
        foreach (var issue in affected)
            change.WriteIssue(issue, CreateIssue.WithDoneState(updated, issue, MoveIssuesTo!, null, context));
        foreach (var workflow in board.Workflows.Values.Where(w => w.Statuses.Contains(StatusId)))
        {
            change.Write(BoardPaths.Workflow(workflow.Id), workflow with
            {
                Statuses = workflow.Statuses.Where(s => s != StatusId).ToList(),
                Transitions = workflow.Transitions.Where(t => t.From != StatusId && t.To != StatusId).ToList(),
            });
        }
        foreach (var definition in board.Boards.Values.Where(b => b.Columns.Any(c => c.Statuses.Contains(StatusId))))
        {
            change.Write(BoardPaths.Board(definition.Id), definition with
            {
                Columns = definition.Columns.Select(c => c with { Statuses = c.Statuses.Where(s => s != StatusId).ToList() }).ToList(),
            });
        }
        return change.Build(affected.Count == 0
            ? L.T($"Status {status.Name} deleted")
            : L.T($"Status {status.Name} deleted, {affected.Count} issues moved to {board.Project.Status(MoveIssuesTo)!.Name}"));
    }
}

/// <summary>Moves a status within a workflow's order; the first status is where new issues start.</summary>
public sealed record MoveStatusInWorkflow(string WorkflowId, string StatusId, int Index) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var workflow = board.Workflows.GetValueOrDefault(WorkflowId) ?? throw new BoardRuleException(L.T("That workflow doesn't exist any more."));
        if (!workflow.Statuses.Contains(StatusId))
            throw new BoardRuleException(L.T("That status isn't part of the workflow."));
        var change = new ChangeBuilder(board, context);
        change.Write(BoardPaths.Workflow(workflow.Id), workflow with { Statuses = Definitions.Move(workflow.Statuses, s => s == StatusId, Index) });
        return change.Build(L.T($"Workflow {workflow.Name}: statuses reordered"));
    }
}

/// <summary>Adds an existing status to a workflow.</summary>
public sealed record AddStatusToWorkflow(string WorkflowId, string StatusId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var workflow = board.Workflows.GetValueOrDefault(WorkflowId) ?? throw new BoardRuleException(L.T("That workflow doesn't exist any more."));
        var status = board.Project.Status(StatusId) ?? throw new BoardRuleException(L.T("That status doesn't exist any more."));
        if (workflow.Statuses.Contains(StatusId))
            throw new BoardRuleException(L.T($"{status.Name} is already part of {workflow.Name}."));
        var change = new ChangeBuilder(board, context);
        change.Write(BoardPaths.Workflow(workflow.Id), workflow with { Statuses = [.. workflow.Statuses, StatusId] });
        return change.Build(L.T($"Workflow {workflow.Name}: {status.Name} added"));
    }
}

/// <summary>Takes a status out of a workflow; its issues (of types using the workflow) move to <see cref="MoveIssuesTo"/>.</summary>
public sealed record RemoveStatusFromWorkflow(string WorkflowId, string StatusId, string? MoveIssuesTo) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var workflow = board.Workflows.GetValueOrDefault(WorkflowId) ?? throw new BoardRuleException(L.T("That workflow doesn't exist any more."));
        var status = board.Project.Status(StatusId) ?? throw new BoardRuleException(L.T("That status doesn't exist any more."));
        if (!workflow.Statuses.Contains(StatusId))
            throw new BoardRuleException(L.T($"{status.Name} isn't part of {workflow.Name}."));
        if (workflow.Statuses.Count == 1)
            throw new BoardRuleException(L.T("A workflow needs at least one status."));
        var types = Definitions.TypesUsing(board, workflow);
        var affected = board.Issues.Values.Where(i => i.Status == StatusId && types.Contains(i.Type)).ToList();
        if (affected.Count > 0 && (MoveIssuesTo is null || MoveIssuesTo == StatusId || !workflow.Statuses.Contains(MoveIssuesTo)))
            throw new BoardRuleException(L.T($"{affected.Count} issues are in {status.Name}: choose another status of {workflow.Name} for them."));

        var change = new ChangeBuilder(board, context);
        change.Write(BoardPaths.Workflow(workflow.Id), workflow with
        {
            Statuses = workflow.Statuses.Where(s => s != StatusId).ToList(),
            Transitions = workflow.Transitions.Where(t => t.From != StatusId && t.To != StatusId).ToList(),
        });
        foreach (var issue in affected)
            change.WriteIssue(issue, CreateIssue.WithDoneState(board, issue, MoveIssuesTo!, null, context));
        return change.Build(L.T($"Workflow {workflow.Name}: {status.Name} removed"));
    }
}

/// <summary>Saves a workflow's transitions (the workflow editor writes the whole set at once).</summary>
public sealed record SetTransitions(string WorkflowId, bool AnyToAny, IReadOnlyList<WorkflowTransition> Transitions) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var workflow = board.Workflows.GetValueOrDefault(WorkflowId) ?? throw new BoardRuleException(L.T("That workflow doesn't exist any more."));
        foreach (var t in Transitions)
        {
            if (!workflow.Statuses.Contains(t.To) || (t.From is not null && !workflow.Statuses.Contains(t.From)))
                throw new BoardRuleException(L.T("A transition uses a status that isn't part of the workflow."));
            if (t.From == t.To)
                throw new BoardRuleException(L.T("A transition goes from one status to another."));
        }
        var distinct = Transitions.DistinctBy(t => (t.From, t.To)).ToList();
        var change = new ChangeBuilder(board, context);
        change.Write(BoardPaths.Workflow(workflow.Id), workflow with { AnyToAny = AnyToAny, Transitions = distinct });
        return change.Build(L.T($"Workflow {workflow.Name}: transitions changed"));
    }
}

/// <summary>Creates a workflow, empty or as a copy of another.</summary>
public sealed record CreateWorkflow(string Name) : IBoardOperation
{
    public string? Id { get; init; }
    public string? CopyOf { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var name = Definitions.RequireName(Name, "workflow");
        Definitions.RequireUnique(board.Workflows.Values, w => w.Name, name, "workflow");
        var change = new ChangeBuilder(board, context);
        var source = CopyOf is null ? null : board.Workflows.GetValueOrDefault(CopyOf) ?? throw new BoardRuleException(L.T("The workflow to copy doesn't exist."));
        var first = board.Project.Statuses.FirstOrDefault() ?? throw new BoardRuleException(L.T("The project has no statuses."));
        var workflow = new Workflow
        {
            Id = Definitions.NewIdFor(change, Id, board.Workflows.Keys),
            Name = name,
            Statuses = source?.Statuses ?? [first.Id],
            AnyToAny = source?.AnyToAny ?? true,
            Transitions = source?.Transitions ?? [],
        };
        change.Write(BoardPaths.Workflow(workflow.Id), workflow);
        return change.Build(L.T($"Workflow {name} created"));
    }
}

public sealed record RenameWorkflow(string WorkflowId, string Name) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var workflow = board.Workflows.GetValueOrDefault(WorkflowId) ?? throw new BoardRuleException(L.T("That workflow doesn't exist any more."));
        var name = Definitions.RequireName(Name, "workflow");
        Definitions.RequireUnique(board.Workflows.Values, w => w.Name, name, "workflow", w => w.Id == WorkflowId);
        var change = new ChangeBuilder(board, context);
        change.Write(BoardPaths.Workflow(workflow.Id), workflow with { Name = name });
        return change.Build(L.T($"Workflow {workflow.Name} renamed to {name}"));
    }
}

/// <summary>Deletes a workflow no issue type uses explicitly (the last one can't go).</summary>
public sealed record DeleteWorkflow(string WorkflowId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var workflow = board.Workflows.GetValueOrDefault(WorkflowId) ?? throw new BoardRuleException(L.T("That workflow doesn't exist any more."));
        if (board.Workflows.Count == 1)
            throw new BoardRuleException(L.T("A board needs at least one workflow."));
        if (board.Project.IssueTypes.FirstOrDefault(t => t.Workflow == WorkflowId) is { } user)
            throw new BoardRuleException(L.T($"{user.Name} issues use {workflow.Name}: give them another workflow first."));
        if (board.DefaultWorkflow?.Id == WorkflowId)
            throw new BoardRuleException(L.T($"{workflow.Name} is the default workflow: make another one the default first."));
        var change = new ChangeBuilder(board, context);
        change.Delete(BoardPaths.Workflow(workflow.Id));
        return change.Build(L.T($"Workflow {workflow.Name} deleted"));
    }
}

/// <summary>
/// Makes a workflow the default (for issue types without their own). Issues in a status it doesn't have move by
/// <see cref="StatusMap"/>, else to its first status.
/// </summary>
public sealed record SetDefaultWorkflow(string WorkflowId) : IBoardOperation
{
    public IReadOnlyDictionary<string, string> StatusMap { get; init; } = new Dictionary<string, string>();

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var workflow = board.Workflows.GetValueOrDefault(WorkflowId) ?? throw new BoardRuleException(L.T("That workflow doesn't exist any more."));
        var change = new ChangeBuilder(board, context);
        var project = board.Project with { DefaultWorkflow = WorkflowId };
        change.WriteProject(project);
        var updated = BoardSnapshotView.WithProject(board, project);
        AssignWorkflow.MoveStrandedIssues(change, updated, board.Issues.Values.Where(i => updated.WorkflowFor(i.Type)?.Id == WorkflowId), StatusMap);
        return change.Build(L.T($"{workflow.Name} is now the default workflow"));
    }
}

/// <summary>
/// Gives an issue type a workflow (null = the default). Its issues in a status the new workflow doesn't have move
/// by <see cref="StatusMap"/> (old → new), else to the new workflow's first status.
/// </summary>
public sealed record AssignWorkflow(string IssueTypeId, string? WorkflowId) : IBoardOperation
{
    public IReadOnlyDictionary<string, string> StatusMap { get; init; } = new Dictionary<string, string>();

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var type = board.Project.IssueType(IssueTypeId) ?? throw new BoardRuleException(L.T("That issue type doesn't exist any more."));
        if (WorkflowId is not null && !board.Workflows.ContainsKey(WorkflowId))
            throw new BoardRuleException(L.T("That workflow doesn't exist any more."));
        var change = new ChangeBuilder(board, context);
        var project = board.Project with
        {
            IssueTypes = Definitions.Replace(board.Project.IssueTypes, t => t.Id == IssueTypeId, t => t with { Workflow = WorkflowId }),
        };
        change.WriteProject(project);
        var updated = BoardSnapshotView.WithProject(board, project);
        var workflow = updated.WorkflowFor(IssueTypeId)!;
        MoveStrandedIssues(change, updated, board.Issues.Values.Where(i => i.Type == IssueTypeId), StatusMap);
        return change.Build(L.T($"{type.Name} issues now follow {workflow.Name}"));
    }

    /// <summary>Moves issues whose status their (new) workflow doesn't have: by the map, else to its first status.</summary>
    internal static void MoveStrandedIssues(ChangeBuilder change, BoardSnapshot updated, IEnumerable<Issue> issues,
        IReadOnlyDictionary<string, string> map)
    {
        foreach (var issue in issues)
        {
            var workflow = updated.WorkflowFor(issue.Type);
            if (workflow is null || workflow.Statuses.Contains(issue.Status))
                continue;
            var target = map.TryGetValue(issue.Status, out var mapped) && workflow.Statuses.Contains(mapped)
                ? mapped
                : workflow.InitialStatus ?? throw new BoardRuleException(L.T($"{workflow.Name} has no statuses."));
            var current = change.Issue(issue.Id)!;
            change.WriteIssue(current, CreateIssue.WithDoneState(updated, current, target, null, change.Context));
        }
    }
}

/// <summary>A snapshot with a changed project, for checks made while an operation is being built.</summary>
internal static class BoardSnapshotView
{
    public static BoardSnapshot WithProject(BoardSnapshot board, BoardProject project) =>
        board.Apply(new BoardChange("", new Dictionary<string, byte[]?> { [BoardPaths.Project] = BoardJson.Serialize(project) }), board.Version);
}
