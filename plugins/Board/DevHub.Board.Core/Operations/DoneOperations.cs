using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Core;

/// <summary>
/// What an issue still lacks before it's done: unticked acceptance criteria and, for standard issues (stories, tasks,
/// bugs), unticked Definition of Done items. Moving it to a Done status then asks first or is refused
/// (<see cref="ProjectSettings.DoneCheck"/>).
/// </summary>
public static class DoneReadiness
{
    /// <summary>One line per missing item ("Criterion: Totals include VAT", "Definition of Done: Code reviewed").</summary>
    public static IReadOnlyList<string> Missing(BoardProject project, Issue issue)
    {
        var missing = issue.Criteria.Where(c => !c.Done).Select(c => L.T("Criterion: ") + c.Text).ToList();
        if (project.IssueType(issue.Type)?.Level == IssueTypeLevel.Standard)
            missing.AddRange(project.DefinitionOfDone.Where(d => !issue.DoneChecks.Contains(d.Id)).Select(d => L.T("Definition of Done: ") + d.Text));
        return missing;
    }

    /// <summary>Whether <paramref name="after"/> is in a Done status and <paramref name="before"/> wasn't.</summary>
    public static bool GoesDone(BoardProject project, Issue before, Issue after) =>
        IsDone(project, after) && !IsDone(project, before);

    /// <summary>
    /// The issues a change moves to Done although they still lack something (to ask before running it): the
    /// operation is tried on <paramref name="board"/> without writing anything. Empty when the check is off.
    /// </summary>
    public static IReadOnlyList<(Issue Issue, IReadOnlyList<string> Missing)> Check(BoardSnapshot board, BoardChange change)
    {
        if (board.Project.Settings.DoneCheck == DoneCheck.Off)
            return [];
        var after = board.Apply(change, board.Version);
        var result = new List<(Issue, IReadOnlyList<string>)>();
        foreach (var path in change.Files.Keys)
        {
            if (BoardPaths.IdOf(path, BoardPaths.IssuesFolder, ".json") is not { } id
                || board.Issue(id) is not { } before || after.Issue(id) is not { } now
                || !GoesDone(after.Project, before, now))
                continue;
            if (Missing(after.Project, now) is { Count: > 0 } missing)
                result.Add((now, missing));
        }
        return result;
    }

    private static bool IsDone(BoardProject project, Issue issue) => project.Status(issue.Status)?.Category == StatusCategory.Done;
}

/// <summary>Adds an acceptance criterion to an issue.</summary>
public sealed record AddCriterion(string IssueId, string Text) : IBoardOperation
{
    public string? Id { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException(L.T("The issue doesn't exist any more."));
        var text = Criteria.RequireText(Text);
        var change = new ChangeBuilder(board, context);
        var item = new CheckItem { Id = Id ?? change.NewId(), Text = text };
        if (issue.Criteria.Any(c => c.Id == item.Id))
            throw new BoardRuleException(L.T("That criterion id is taken."));
        change.WriteIssue(issue, issue with { Criteria = [.. issue.Criteria, item] });
        change.AddHistory(issue.Id, "criteria", null, text);
        return change.Build(L.T($"{issue.Key}: acceptance criterion added"));
    }
}

public sealed record EditCriterion(string IssueId, string CriterionId, string Text) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var (issue, item) = Criteria.Find(board, IssueId, CriterionId);
        var text = Criteria.RequireText(Text);
        var change = new ChangeBuilder(board, context);
        change.WriteIssue(issue, issue with { Criteria = Definitions.Replace(issue.Criteria, c => c.Id == CriterionId, c => c with { Text = text }) });
        change.AddHistory(issue.Id, "criteria", item.Text, text);
        return change.Build(L.T($"{issue.Key}: acceptance criterion changed"));
    }
}

/// <summary>Ticks or unticks an acceptance criterion.</summary>
public sealed record CheckCriterion(string IssueId, string CriterionId, bool Done) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var (issue, item) = Criteria.Find(board, IssueId, CriterionId);
        var change = new ChangeBuilder(board, context);
        if (item.Done == Done)
            return change.Build(L.T($"{issue.Key}: nothing to change"));
        change.WriteIssue(issue, issue with { Criteria = Definitions.Replace(issue.Criteria, c => c.Id == CriterionId, c => c with { Done = Done }) });
        change.AddHistory(issue.Id, "criteria-met", Done ? null : item.Text, Done ? item.Text : null);
        return change.Build($"{issue.Key}: \"{item.Text}\" {(Done ? L.T("met") : L.T("not met"))}");
    }
}

public sealed record RemoveCriterion(string IssueId, string CriterionId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var (issue, item) = Criteria.Find(board, IssueId, CriterionId);
        var change = new ChangeBuilder(board, context);
        change.WriteIssue(issue, issue with { Criteria = issue.Criteria.Where(c => c.Id != CriterionId).ToList() });
        change.AddHistory(issue.Id, "criteria", item.Text, null);
        return change.Build(L.T($"{issue.Key}: acceptance criterion removed"));
    }
}

public sealed record MoveCriterion(string IssueId, string CriterionId, int Index) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var (issue, _) = Criteria.Find(board, IssueId, CriterionId);
        var change = new ChangeBuilder(board, context);
        change.WriteIssue(issue, issue with { Criteria = Definitions.Move(issue.Criteria, c => c.Id == CriterionId, Index) });
        return change.Build(L.T($"{issue.Key}: acceptance criteria reordered"));
    }
}

/// <summary>Ticks or unticks one Definition of Done item on an issue.</summary>
public sealed record CheckDoneItem(string IssueId, string ItemId, bool Done) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException(L.T("The issue doesn't exist any more."));
        var item = board.Project.DefinitionOfDone.FirstOrDefault(d => d.Id == ItemId)
                   ?? throw new BoardRuleException(L.T("That Definition of Done item doesn't exist any more."));
        var change = new ChangeBuilder(board, context);
        if (issue.DoneChecks.Contains(ItemId) == Done)
            return change.Build(L.T($"{issue.Key}: nothing to change"));
        var checks = Done ? [.. issue.DoneChecks, ItemId] : issue.DoneChecks.Where(i => i != ItemId).ToList();
        change.WriteIssue(issue, issue with { DoneChecks = checks });
        change.AddHistory(issue.Id, "done-check", Done ? null : item.Text, Done ? item.Text : null);
        return change.Build($"{issue.Key}: \"{item.Text}\" {(Done ? L.T("done") : L.T("not done"))}");
    }
}

/// <summary>Adds an item to the project's Definition of Done.</summary>
public sealed record AddDoneItem(string Text) : IBoardOperation
{
    public string? Id { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var text = Criteria.RequireText(Text);
        Definitions.RequireUnique(board.Project.DefinitionOfDone, d => d.Text, text, L.T("Definition of Done item"));
        var change = new ChangeBuilder(board, context);
        var item = new DoneItem { Id = Definitions.NewIdFor(change, Id, board.Project.DefinitionOfDone.Select(d => d.Id)), Text = text };
        change.WriteProject(board.Project with { DefinitionOfDone = [.. board.Project.DefinitionOfDone, item] });
        return change.Build(L.T($"Definition of Done: \"{text}\" added"));
    }
}

public sealed record EditDoneItem(string ItemId, string Text) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        if (board.Project.DefinitionOfDone.All(d => d.Id != ItemId))
            throw new BoardRuleException(L.T("That Definition of Done item doesn't exist any more."));
        var text = Criteria.RequireText(Text);
        Definitions.RequireUnique(board.Project.DefinitionOfDone, d => d.Text, text, L.T("Definition of Done item"), d => d.Id == ItemId);
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with
        {
            DefinitionOfDone = Definitions.Replace(board.Project.DefinitionOfDone, d => d.Id == ItemId, d => d with { Text = text }),
        });
        return change.Build(L.T("Definition of Done changed"));
    }
}

/// <summary>Removes a Definition of Done item (ticks on issues for it are ignored from then on).</summary>
public sealed record RemoveDoneItem(string ItemId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var item = board.Project.DefinitionOfDone.FirstOrDefault(d => d.Id == ItemId)
                   ?? throw new BoardRuleException(L.T("That Definition of Done item doesn't exist any more."));
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with { DefinitionOfDone = board.Project.DefinitionOfDone.Where(d => d.Id != ItemId).ToList() });
        return change.Build(L.T($"Definition of Done: \"{item.Text}\" removed"));
    }
}

public sealed record MoveDoneItem(string ItemId, int Index) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with { DefinitionOfDone = Definitions.Move(board.Project.DefinitionOfDone, d => d.Id == ItemId, Index) });
        return change.Build(L.T("Definition of Done reordered"));
    }
}

/// <summary>What moving an unfinished issue to Done does: nothing, ask, or refuse.</summary>
public sealed record SetDoneCheck(DoneCheck Check) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with { Settings = board.Project.Settings with { DoneCheck = Check } });
        return change.Build(L.T("Definition of Done check changed"));
    }
}

/// <summary>Sets someone's role on the team ("Product Owner", "Scrum Master", "Developer"…); empty clears it.</summary>
public sealed record SetPersonRole(string PersonId, string? Role) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var person = board.Project.Person(PersonId) ?? throw new BoardRuleException(L.T("That person isn't on the board any more."));
        var role = string.IsNullOrWhiteSpace(Role) ? null : Role.Trim();
        var change = new ChangeBuilder(board, context);
        change.WriteProject(board.Project with { People = Definitions.Replace(board.Project.People, p => p.Id == PersonId, p => p with { Role = role }) });
        return change.Build(role is null ? L.T($"{person.Name}: no role") : $"{person.Name}: {role}");
    }
}

internal static class Criteria
{
    public const int MaxLength = 500;

    public static string RequireText(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            throw new BoardRuleException(L.T("The text is empty."));
        if (trimmed.Length > MaxLength)
            throw new BoardRuleException(L.T($"Keep it under {MaxLength} characters."));
        return trimmed;
    }

    public static (Issue Issue, CheckItem Item) Find(BoardSnapshot board, string issueId, string criterionId)
    {
        var issue = board.Issue(issueId) ?? throw new BoardRuleException(L.T("The issue doesn't exist any more."));
        var item = issue.Criteria.FirstOrDefault(c => c.Id == criterionId) ?? throw new BoardRuleException(L.T("That criterion doesn't exist any more."));
        return (issue, item);
    }
}
