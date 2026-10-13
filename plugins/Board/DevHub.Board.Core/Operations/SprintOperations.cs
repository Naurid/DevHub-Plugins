using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;

namespace DevHub.Board.Core;

/// <summary>
/// Sprint rules (the data lives in Board.Core; Board.Scrum is the screens). One sprint runs at a time; issues are
/// planned into a sprint by setting their sprint field (sub-tasks follow their parent).
/// </summary>
internal static class Sprints
{
    public static Sprint Require(BoardSnapshot board, string id) =>
        board.Sprints.GetValueOrDefault(id) ?? throw new BoardRuleException("That sprint doesn't exist any more.");

    public static IEnumerable<Issue> IssuesIn(BoardSnapshot board, string sprintId) =>
        board.Issues.Values.Where(i => i.Sprint == sprintId);

    /// <summary>Issues counted for a sprint's points: standard issues (sub-tasks count through their parent).</summary>
    public static IReadOnlyList<SprintIssuePoints> PointsOf(BoardSnapshot board, IEnumerable<Issue> issues) =>
        issues.Where(i => board.LevelOf(i) == IssueTypeLevel.Standard)
            .OrderBy(i => i.Rank, StringComparer.Ordinal)
            .Select(i => new SprintIssuePoints { Issue = i.Id, Points = i.StoryPoints }).ToList();

    public static void CheckDates(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is { } s && end is { } e && e <= s)
            throw new BoardRuleException("A sprint ends after it starts.");
    }
}

/// <summary>Creates a future sprint, by default named "&lt;KEY&gt; Sprint &lt;n&gt;", after the others.</summary>
public sealed record CreateSprint : IBoardOperation
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string Goal { get; init; } = "";

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var change = new ChangeBuilder(board, context);
        var number = board.Sprints.Count + 1;
        var name = string.IsNullOrWhiteSpace(Name) ? $"{board.Project.Key} Sprint {number}" : Name.Trim();
        while (string.IsNullOrWhiteSpace(Name) && board.Sprints.Values.Any(s => s.Name == name))
            name = $"{board.Project.Key} Sprint {++number}";
        Definitions.RequireUnique(board.Sprints.Values, s => s.Name, name, "sprint");
        var sprint = new Sprint
        {
            Id = Definitions.NewIdFor(change, Id, board.Sprints.Keys),
            Name = name,
            Goal = Goal.Trim(),
            Sequence = board.Sprints.Values.Select(s => s.Sequence).DefaultIfEmpty(0).Max() + 1,
        };
        change.Write(BoardPaths.Sprint(sprint.Id), sprint);
        return change.Build($"Sprint {name} created");
    }
}

/// <summary>Changes a sprint's name, goal, dates and capacity.</summary>
public sealed record EditSprint(string SprintId, string Name, string Goal, DateTimeOffset? Start, DateTimeOffset? End, double? Capacity) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var sprint = Sprints.Require(board, SprintId);
        var name = Definitions.RequireName(Name, "sprint");
        Definitions.RequireUnique(board.Sprints.Values, s => s.Name, name, "sprint", s => s.Id == SprintId);
        Sprints.CheckDates(Start, End);
        if (Capacity is < 0)
            throw new BoardRuleException("Capacity can't be negative.");
        var change = new ChangeBuilder(board, context);
        change.Write(BoardPaths.Sprint(sprint.Id), sprint with { Name = name, Goal = Goal.Trim(), Start = Start, End = End, Capacity = Capacity });
        return change.Build($"Sprint {sprint.Name} changed");
    }
}

/// <summary>Starts a future sprint: dates and goal, and a record of what was committed (its issues and points).</summary>
public sealed record StartSprint(string SprintId, DateTimeOffset Start, DateTimeOffset End) : IBoardOperation
{
    public string? Goal { get; init; }
    public string? Name { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var sprint = Sprints.Require(board, SprintId);
        if (sprint.State != SprintState.Future)
            throw new BoardRuleException($"{sprint.Name} has already started.");
        if (board.Sprints.Values.FirstOrDefault(s => s.State == SprintState.Active) is { } running)
            throw new BoardRuleException($"{running.Name} is still running: complete it first.");
        Sprints.CheckDates(Start, End);
        var name = Name is null ? sprint.Name : Definitions.RequireName(Name, "sprint");
        var issues = Sprints.IssuesIn(board, sprint.Id).ToList();
        if (issues.Count == 0)
            throw new BoardRuleException($"{sprint.Name} has no issues yet: plan some first.");
        var change = new ChangeBuilder(board, context);
        change.Write(BoardPaths.Sprint(sprint.Id), sprint with
        {
            Name = name,
            Goal = Goal?.Trim() ?? sprint.Goal,
            State = SprintState.Active,
            Start = Start,
            End = End,
            Committed = Sprints.PointsOf(board, issues),
        });
        return change.Build($"Sprint {name} started");
    }
}

/// <summary>Where a completing sprint's unfinished issues go.</summary>
public enum OpenIssuesGoTo
{
    Backlog,

    /// <summary>To <see cref="CompleteSprint.NextSprint"/> (an existing future sprint).</summary>
    NextSprint,

    /// <summary>To a new future sprint created now.</summary>
    NewSprint,
}

/// <summary>
/// Completes the running sprint: records what was finished and what wasn't, closes it, and moves the unfinished
/// issues to the backlog or another sprint. Finished issues keep the sprint (reports read it).
/// </summary>
public sealed record CompleteSprint(string SprintId, OpenIssuesGoTo MoveTo = OpenIssuesGoTo.Backlog) : IBoardOperation
{
    public string? NextSprint { get; init; }

    /// <summary>The id the new sprint gets (with <see cref="OpenIssuesGoTo.NewSprint"/>).</summary>
    public string? NewSprintId { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var sprint = Sprints.Require(board, SprintId);
        if (sprint.State != SprintState.Active)
            throw new BoardRuleException($"{sprint.Name} isn't running.");
        var change = new ChangeBuilder(board, context);

        string? target = null;
        switch (MoveTo)
        {
            case OpenIssuesGoTo.NextSprint:
                var next = NextSprint is null ? null : board.Sprints.GetValueOrDefault(NextSprint);
                if (next is null || next.State != SprintState.Future)
                    throw new BoardRuleException("Choose a sprint that hasn't started for the unfinished issues.");
                target = next.Id;
                break;
            case OpenIssuesGoTo.NewSprint:
                var created = new CreateSprint { Id = NewSprintId }.Apply(board, context);
                foreach (var (path, bytes) in created.Files)
                    change.WriteBytes(path, bytes!);
                target = BoardPaths.IdOf(created.Files.Keys.Single(), BoardPaths.SprintsFolder, ".json");
                break;
        }

        var issues = Sprints.IssuesIn(board, sprint.Id).ToList();
        var done = issues.Where(board.IsDone).ToList();
        var open = issues.Where(i => !board.IsDone(i)).ToList();
        foreach (var issue in open)
            change.WriteIssue(issue, issue with { Sprint = target });

        change.Write(BoardPaths.Sprint(sprint.Id), sprint with
        {
            State = SprintState.Closed,
            Completed = context.Now,
            CompletedIssues = Sprints.PointsOf(board, done),
            NotCompleted = Sprints.PointsOf(board, open),
        });
        var where = target is null ? "the backlog" : board.Sprints.GetValueOrDefault(target)?.Name ?? "a new sprint";
        return change.Build(open.Count == 0
            ? $"Sprint {sprint.Name} completed"
            : $"Sprint {sprint.Name} completed, {open.Count} unfinished issues moved to {where}");
    }
}

/// <summary>Deletes a sprint that hasn't started; its issues go back to the backlog.</summary>
public sealed record DeleteSprint(string SprintId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var sprint = Sprints.Require(board, SprintId);
        if (sprint.State != SprintState.Future)
            throw new BoardRuleException(sprint.State == SprintState.Active
                ? $"{sprint.Name} is running: complete it instead."
                : $"{sprint.Name} is finished: it stays for the reports.");
        var change = new ChangeBuilder(board, context);
        foreach (var issue in Sprints.IssuesIn(board, sprint.Id))
            change.WriteIssue(issue, issue with { Sprint = null });
        change.Delete(BoardPaths.Sprint(sprint.Id));
        return change.Build($"Sprint {sprint.Name} deleted");
    }
}

/// <summary>Moves a future sprint up or down among the planned ones.</summary>
public sealed record MoveSprint(string SprintId, int Step) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var sprint = Sprints.Require(board, SprintId);
        var future = board.Sprints.Values.Where(s => s.State == SprintState.Future).OrderBy(s => s.Sequence).ToList();
        var index = future.FindIndex(s => s.Id == SprintId);
        var other = index + Step;
        if (index < 0 || other < 0 || other >= future.Count || Step == 0)
            return new BoardChange("No change", new Dictionary<string, byte[]?>());
        var change = new ChangeBuilder(board, context);
        change.Write(BoardPaths.Sprint(sprint.Id), sprint with { Sequence = future[other].Sequence });
        change.Write(BoardPaths.Sprint(future[other].Id), future[other] with { Sequence = sprint.Sequence });
        return change.Build("Sprints reordered");
    }
}

/// <summary>
/// Plans an issue: into a sprint (or the backlog, null) and ranked between two others, in one change (a drop in
/// sprint planning). Sub-tasks follow their parent.
/// </summary>
public sealed record PlanIssue(string IssueId, string? Sprint, string? Above, string? Below) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException("The issue doesn't exist any more.");
        if (Sprint is not null && Sprints.Require(board, Sprint).State == SprintState.Closed)
            throw new BoardRuleException("That sprint is finished.");
        var files = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        var messages = new List<string>();
        var current = board;
        if (issue.Sprint != Sprint)
            Merge(new EditIssue(IssueId, Fields.IssueFields.Sprint, new Fields.FieldValue.Text(Sprint)).Apply(current, context));
        if (Above is not null || Below is not null)
            Merge(new RankIssue(IssueId, Above, Below).Apply(current, context));
        return new BoardChange(messages.Count == 0 ? "No change" : string.Join("; ", messages), files);

        void Merge(BoardChange step)
        {
            foreach (var (path, bytes) in step.Files)
                files[path] = bytes;
            messages.Add(step.Message);
            current = current.Apply(step, current.Version);
        }
    }
}
