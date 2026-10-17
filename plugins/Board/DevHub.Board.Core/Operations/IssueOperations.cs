using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;

namespace DevHub.Board.Core;

/// <summary>
/// Creates an issue: the next key, the workflow's first status (or <see cref="Status"/>), the default priority,
/// you as reporter, ranked last. <see cref="Fields"/> sets any other field (<see cref="IssueFields"/> names).
/// Pass an <see cref="Id"/> to know the new issue's id.
/// </summary>
public sealed record CreateIssue(string Type, string Summary) : IBoardOperation
{
    public string? Id { get; init; }
    public string? Parent { get; init; }
    public string Description { get; init; } = "";
    public string? Status { get; init; }
    public IReadOnlyList<(string Field, FieldValue Value)> Fields { get; init; } = [];

    /// <summary>Rank it directly above this issue instead of last.</summary>
    public string? RankAbove { get; init; }

    /// <summary>Its acceptance criteria, one per line of text (empty lines are skipped).</summary>
    public IReadOnlyList<string> Criteria { get; init; } = [];

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var project = board.Project;
        var type = project.IssueType(Type) ?? throw new BoardRuleException($"There's no issue type \"{Type}\".");
        var id = Id ?? new ChangeBuilder(board, context).NewId();
        if (!BoardIds.IsValid(id) || board.Issues.ContainsKey(id))
            throw new BoardRuleException($"The id \"{id}\" can't be used.");

        var workflow = board.WorkflowFor(type.Id) ?? throw new BoardRuleException("The board has no workflow.");
        var status = Status ?? workflow.InitialStatus ?? throw new BoardRuleException("The workflow has no statuses.");
        if (!workflow.Statuses.Contains(status))
            throw new BoardRuleException($"\"{status}\" isn't a status of the {workflow.Name} workflow.");

        IssueFields.ValidateParent(board, null, type.Id, Parent);
        var parent = board.Issue(Parent);

        var issue = new Issue
        {
            Id = id,
            Key = IssueKey.Format(project.Key, project.NextNumber),
            Type = type.Id,
            Summary = Summary,
            Description = Description,
            Status = status,
            Priority = project.DefaultPriorityId,
            Parent = Parent,
            Reporter = project.Person(context.Who)?.Id,
            // A sub-task is in its parent's sprint.
            Sprint = type.Level == IssueTypeLevel.Subtask ? parent?.Sprint : null,
            Rank = RankAbove is not null && board.Issue(RankAbove) is { } above
                ? RankFor(board, above)
                : Rank.Between(MaxRank(board), null),
        };
        issue = WithDoneState(board, issue, status, null, context);

        IssueFields.Validate(board, issue, IssueFields.Summary, new FieldValue.Text(Summary));
        foreach (var (field, value) in Fields)
        {
            IssueFields.Validate(board, issue, field, value);
            issue = IssueFields.Set(issue, field, value);
            if (field == IssueFields.OriginalEstimate && issue.Time.RemainingMinutes is null)
                issue = issue with { Time = issue.Time with { RemainingMinutes = issue.Time.OriginalMinutes } };
        }

        var change = new ChangeBuilder(board, context);
        var criteria = Criteria.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => new CheckItem { Id = change.NewId(), Text = Core.Criteria.RequireText(c) });
        issue = issue with { Criteria = [.. criteria] };
        change.WriteProject(project with { NextNumber = project.NextNumber + 1 });
        change.WriteIssue(null, issue);
        return change.Build($"{issue.Key}: created \"{issue.Summary}\"");
    }

    internal static string? MaxRank(BoardSnapshot board) =>
        board.Issues.Values.Select(i => i.Rank).Where(Rank.IsValid).Max(StringComparer.Ordinal);

    // Directly above an issue: between it and the issue ranked before it.
    private static string RankFor(BoardSnapshot board, Issue above)
    {
        var before = board.Issues.Values.Select(i => i.Rank)
            .Where(r => Rank.IsValid(r) && string.CompareOrdinal(r, above.Rank) < 0).Max(StringComparer.Ordinal);
        return Rank.IsValid(above.Rank) ? Rank.Between(before, above.Rank) : Rank.Between(MaxRank(board), null);
    }

    /// <summary>Sets resolution and resolved time to match the status's category.</summary>
    internal static Issue WithDoneState(BoardSnapshot board, Issue issue, string status, string? resolution, OperationContext context)
    {
        var done = board.Project.Status(status)?.Category == StatusCategory.Done;
        if (!done)
            return issue with { Status = status, Resolution = null, Resolved = null };
        var chosen = resolution ?? issue.Resolution ?? board.Project.Resolutions.FirstOrDefault()?.Id;
        if (resolution is not null && board.Project.Resolution(resolution) is null)
            throw new BoardRuleException($"There's no resolution \"{resolution}\".");
        return issue with { Status = status, Resolution = chosen, Resolved = issue.Resolved ?? context.Now };
    }
}

/// <summary>Sets one field of one issue (<see cref="IssueFields"/> names; status changes through <see cref="TransitionIssue"/>).</summary>
public sealed record EditIssue(string IssueId, string Field, FieldValue Value) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context) =>
        new BulkEdit([IssueId], [(Field, Value)]).Apply(board, context);
}

/// <summary>Sets fields of several issues at once (bulk edit); all or nothing.</summary>
public sealed record BulkEdit(IReadOnlyList<string> IssueIds, IReadOnlyList<(string Field, FieldValue Value)> Changes) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        if (IssueIds.Count == 0 || Changes.Count == 0)
            throw new BoardRuleException("Nothing to change.");
        var change = new ChangeBuilder(board, context);
        var keys = new List<string>();
        foreach (var id in IssueIds.Distinct())
        {
            var before = change.Issue(id) ?? throw new BoardRuleException("The issue doesn't exist any more.");
            var after = before;
            foreach (var (field, value) in Changes)
            {
                IssueFields.Validate(board, after, field, value);
                after = IssueFields.Set(after, field, value);
                if (field == IssueFields.OriginalEstimate && after.Time.RemainingMinutes is null)
                    after = after with { Time = after.Time with { RemainingMinutes = after.Time.OriginalMinutes } };
            }
            if (BoardJson.Serialize(after).AsSpan().SequenceEqual(BoardJson.Serialize(before)))
                continue;
            change.WriteIssue(before, after);
            keys.Add(after.Key);

            // Sub-tasks move with their parent between sprints.
            if (after.Sprint != before.Sprint)
            {
                foreach (var sub in board.ChildrenOf(id).Where(s => board.LevelOf(s) == IssueTypeLevel.Subtask))
                {
                    var current = change.Issue(sub.Id)!;
                    if (current.Sprint != after.Sprint)
                        change.WriteIssue(current, current with { Sprint = after.Sprint });
                }
            }
        }
        var fields = string.Join(", ", Changes.Select(c => IssueFields.DisplayName(c.Field, board.Project).ToLowerInvariant()));
        var who = keys.Count == 1 ? keys[0] : $"{keys.Count} issues";
        return change.Build(keys.Count == 0 ? "No change" : $"{who}: {fields} changed");
    }
}

/// <summary>
/// Moves an issue to another status, if its workflow allows it. A Done status sets a resolution (the given one,
/// else the first) and the resolved time; leaving Done clears both.
/// </summary>
public sealed record TransitionIssue(string IssueId, string Status) : IBoardOperation
{
    public string? Resolution { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var before = board.Issue(IssueId) ?? throw new BoardRuleException("The issue doesn't exist any more.");
        var target = board.Project.Status(Status) ?? throw new BoardRuleException($"There's no status \"{Status}\".");
        var workflow = board.WorkflowFor(before.Type) ?? throw new BoardRuleException("The board has no workflow.");
        if (before.Status == Status && Resolution is null)
            throw new BoardRuleException($"{before.Key} is already in {target.Name}.");
        if (before.Status != Status && !workflow.Allows(before.Status, Status))
        {
            var from = board.Project.Status(before.Status)?.Name ?? before.Status;
            throw new BoardRuleException(workflow.Statuses.Contains(Status)
                ? $"The {workflow.Name} workflow doesn't allow {from} → {target.Name}."
                : $"{target.Name} isn't part of the {workflow.Name} workflow.");
        }

        var after = CreateIssue.WithDoneState(board, before, Status, Resolution, context);
        var change = new ChangeBuilder(board, context);
        change.WriteIssue(before, after);
        var fromName = board.Project.Status(before.Status)?.Name ?? before.Status;
        return change.Build($"{before.Key}: status {fromName} → {target.Name}");
    }
}

/// <summary>
/// Ranks an issue between two others (<see cref="Above"/> directly above it, <see cref="Below"/> directly below;
/// either may be null for the top or bottom). Rewrites one file, or every rank when they've run out of room
/// (<see cref="Model.Rank.Spread"/>).
/// </summary>
public sealed record RankIssue(string IssueId, string? Above, string? Below) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException("The issue doesn't exist any more.");
        var above = Above is null ? null : board.Issue(Above) ?? throw new BoardRuleException("The issue above doesn't exist any more.");
        var below = Below is null ? null : board.Issue(Below) ?? throw new BoardRuleException("The issue below doesn't exist any more.");
        if (above?.Id == issue.Id || below?.Id == issue.Id)
            throw new BoardRuleException("An issue can't be ranked next to itself.");

        var change = new ChangeBuilder(board, context);
        var lo = above?.Rank;
        var hi = below?.Rank;
        if (above is null && below is not null)
            lo = Neighbour(board, below, issue.Id, before: true);
        if (below is null && above is not null)
            hi = Neighbour(board, above, issue.Id, before: false);

        string rank;
        if ((lo is null || Rank.IsValid(lo)) && (hi is null || Rank.IsValid(hi))
            && (lo is null || hi is null || string.CompareOrdinal(lo, hi) < 0))
        {
            rank = Rank.Between(lo, hi);
        }
        else
        {
            // Duplicate or broken ranks (a merge, a hand edit): give everyone fresh ones in today's order, then retry.
            var ordered = board.Issues.Values.Where(i => i.Id != issue.Id)
                .OrderBy(i => i.Rank, StringComparer.Ordinal).ThenBy(i => i.Key, StringComparer.Ordinal).ToList();
            var fresh = Rank.Spread(ordered.Count);
            var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < ordered.Count; i++)
            {
                renamed[ordered[i].Id] = fresh[i];
                if (ordered[i].Rank != fresh[i])
                    change.WriteIssue(ordered[i], ordered[i] with { Rank = fresh[i] });
            }
            var newLo = above is not null ? renamed[above.Id] : below is not null ? PreviousOf(ordered, renamed, below.Id) : null;
            var newHi = below is not null ? renamed[below.Id] : above is not null ? NextOf(ordered, renamed, above.Id) : null;
            if (above is null && below is null)
                newLo = fresh.LastOrDefault();
            rank = Rank.Between(newLo, newHi);
        }

        change.WriteIssue(change.Issue(issue.Id), issue with { Rank = rank });
        return change.Build($"{issue.Key}: ranked");
    }

    // The rank just before (or after) an issue, skipping the one being moved.
    private static string? Neighbour(BoardSnapshot board, Issue of, string moving, bool before)
    {
        var ranks = board.Issues.Values.Where(i => i.Id != moving && i.Id != of.Id && Rank.IsValid(i.Rank)).Select(i => i.Rank);
        return before
            ? ranks.Where(r => string.CompareOrdinal(r, of.Rank) < 0).Max(StringComparer.Ordinal)
            : ranks.Where(r => string.CompareOrdinal(r, of.Rank) > 0).Min(StringComparer.Ordinal);
    }

    private static string? PreviousOf(List<Issue> ordered, Dictionary<string, string> ranks, string id)
    {
        var index = ordered.FindIndex(i => i.Id == id);
        return index > 0 ? ranks[ordered[index - 1].Id] : null;
    }

    private static string? NextOf(List<Issue> ordered, Dictionary<string, string> ranks, string id)
    {
        var index = ordered.FindIndex(i => i.Id == id);
        return index >= 0 && index + 1 < ordered.Count ? ranks[ordered[index + 1].Id] : null;
    }
}

/// <summary>
/// Deletes an issue, its change log and attachments, and links to it from other issues. An epic's issues stay
/// (without an epic); sub-tasks are deleted with their parent only when <see cref="WithSubtasks"/> is set.
/// </summary>
public sealed record DeleteIssue(string IssueId, bool WithSubtasks = false) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException("The issue doesn't exist any more.");
        var change = new ChangeBuilder(board, context);
        var deleted = new HashSet<string>(StringComparer.Ordinal) { issue.Id };
        foreach (var child in board.ChildrenOf(issue.Id))
        {
            if (board.LevelOf(child) == IssueTypeLevel.Subtask)
            {
                if (!WithSubtasks)
                    throw new BoardRuleException($"{issue.Key} has sub-tasks: delete them too, or move them first.");
                deleted.Add(child.Id);
            }
            else
            {
                change.WriteIssue(child, child with { Parent = null });
            }
        }
        foreach (var other in board.Issues.Values.Where(i => !deleted.Contains(i.Id) && i.Links.Any(l => deleted.Contains(l.Target))))
        {
            var current = change.Issue(other.Id)!;
            change.WriteIssue(current, current with { Links = current.Links.Where(l => !deleted.Contains(l.Target)).ToList() });
            foreach (var link in other.Links.Where(l => deleted.Contains(l.Target)))
                change.AddHistory(other.Id, "link", $"{link.Type}:{link.Target}", null);
        }
        foreach (var id in deleted)
            change.DeleteIssue(board.Issue(id)!);
        return change.Build(deleted.Count == 1 ? $"{issue.Key}: deleted" : $"{issue.Key}: deleted with {deleted.Count - 1} sub-tasks");
    }
}
