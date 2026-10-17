using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Core;

/// <summary>Adds a comment (Markdown). Pass an <see cref="Id"/> to know the new comment's id.</summary>
public sealed record AddComment(string IssueId, string Body) : IBoardOperation
{
    public string? Id { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException(L.T("The issue doesn't exist any more."));
        if (string.IsNullOrWhiteSpace(Body))
            throw new BoardRuleException(L.T("The comment is empty."));
        var change = new ChangeBuilder(board, context);
        var comment = new Comment { Id = Id ?? change.NewId(), Author = context.Who, Created = context.Now, Body = Body.Trim() };
        if (issue.Comments.Any(c => c.Id == comment.Id))
            throw new BoardRuleException(L.T("That comment id is taken."));
        change.WriteIssue(issue, issue with { Comments = [.. issue.Comments, comment] });
        change.AddHistory(issue.Id, "comment", null, comment.Id);
        return change.Build(L.T($"{issue.Key}: comment added"));
    }
}

/// <summary>Changes a comment's text.</summary>
public sealed record EditComment(string IssueId, string CommentId, string Body) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException(L.T("The issue doesn't exist any more."));
        var comment = issue.Comments.FirstOrDefault(c => c.Id == CommentId) ?? throw new BoardRuleException(L.T("The comment doesn't exist any more."));
        if (string.IsNullOrWhiteSpace(Body))
            throw new BoardRuleException(L.T("The comment is empty."));
        var change = new ChangeBuilder(board, context);
        var edited = comment with { Body = Body.Trim(), Edited = context.Now };
        change.WriteIssue(issue, issue with { Comments = issue.Comments.Select(c => c.Id == CommentId ? edited : c).ToList() });
        return change.Build(L.T($"{issue.Key}: comment edited"));
    }
}

public sealed record DeleteComment(string IssueId, string CommentId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException(L.T("The issue doesn't exist any more."));
        if (issue.Comments.All(c => c.Id != CommentId))
            throw new BoardRuleException(L.T("The comment doesn't exist any more."));
        var change = new ChangeBuilder(board, context);
        change.WriteIssue(issue, issue with { Comments = issue.Comments.Where(c => c.Id != CommentId).ToList() });
        change.AddHistory(issue.Id, "comment", CommentId, null);
        return change.Build(L.T($"{issue.Key}: comment deleted"));
    }
}

/// <summary>Links two issues ("DEV-1 blocks DEV-2"); the link is stored on <see cref="IssueId"/>.</summary>
public sealed record AddLink(string IssueId, string LinkType, string Target) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException(L.T("The issue doesn't exist any more."));
        var target = board.Issue(Target) ?? throw new BoardRuleException(L.T("The other issue doesn't exist."));
        var type = board.Project.LinkType(LinkType) ?? throw new BoardRuleException(L.T($"There's no link type \"{LinkType}\"."));
        if (target.Id == issue.Id)
            throw new BoardRuleException(L.T("An issue can't be linked to itself."));
        if (issue.Links.Any(l => l.Type == type.Id && l.Target == target.Id)
            || target.Links.Any(l => l.Type == type.Id && l.Target == issue.Id && type.Outward == type.Inward))
            throw new BoardRuleException($"{issue.Key} already {type.Outward} {target.Key}.");

        var change = new ChangeBuilder(board, context);
        change.WriteIssue(issue, issue with { Links = [.. issue.Links, new IssueLink { Type = type.Id, Target = target.Id }] });
        change.AddHistory(issue.Id, "link", null, $"{type.Id}:{target.Id}");
        change.AddHistory(target.Id, "link", null, $"{type.Id}<:{issue.Id}");
        return change.Build($"{issue.Key} {type.Outward} {target.Key}");
    }
}

/// <summary>Removes a link between two issues, whichever of them holds it.</summary>
public sealed record RemoveLink(string IssueId, string LinkType, string Target) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException(L.T("The issue doesn't exist any more."));
        var target = board.Issue(Target) ?? throw new BoardRuleException(L.T("The other issue doesn't exist."));
        var (holder, other) = issue.Links.Any(l => l.Type == LinkType && l.Target == target.Id) ? (issue, target)
            : target.Links.Any(l => l.Type == LinkType && l.Target == issue.Id) ? (target, issue)
            : throw new BoardRuleException(L.T("Those issues aren't linked that way."));

        var change = new ChangeBuilder(board, context);
        change.WriteIssue(holder, holder with { Links = holder.Links.Where(l => !(l.Type == LinkType && l.Target == other.Id)).ToList() });
        change.AddHistory(holder.Id, "link", $"{LinkType}:{other.Id}", null);
        change.AddHistory(other.Id, "link", $"{LinkType}<:{holder.Id}", null);
        return change.Build(L.T($"{holder.Key}: link to {other.Key} removed"));
    }
}

/// <summary>How logging work changes the remaining estimate.</summary>
public enum RemainingAdjustment
{
    /// <summary>Lower it by the time spent (not below zero). Jira's default.</summary>
    Auto,

    /// <summary>Leave it as it is.</summary>
    Leave,

    /// <summary>Set it to <see cref="LogWork.NewRemainingMinutes"/>.</summary>
    Set,
}

/// <summary>Logs work on an issue.</summary>
public sealed record LogWork(string IssueId, int Minutes, DateTimeOffset Started) : IBoardOperation
{
    public string Note { get; init; } = "";
    public RemainingAdjustment Remaining { get; init; } = RemainingAdjustment.Auto;
    public int? NewRemainingMinutes { get; init; }
    public string? Id { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException(L.T("The issue doesn't exist any more."));
        if (Minutes <= 0)
            throw new BoardRuleException(L.T("The time spent must be more than zero."));
        var change = new ChangeBuilder(board, context);
        var log = new WorkLog { Id = Id ?? change.NewId(), Author = context.Who, Started = Started, Minutes = Minutes, Note = Note.Trim() };
        var remaining = Remaining switch
        {
            RemainingAdjustment.Auto => issue.Time.RemainingMinutes is { } r ? Math.Max(0, r - Minutes) : (int?)null,
            RemainingAdjustment.Set => NewRemainingMinutes is >= 0 ? NewRemainingMinutes
                : throw new BoardRuleException(L.T("Say what the remaining estimate is.")),
            _ => issue.Time.RemainingMinutes,
        };
        var time = issue.Time with { Log = [.. issue.Time.Log, log], RemainingMinutes = remaining };
        change.WriteIssue(issue, issue with { Time = time });
        change.AddHistory(issue.Id, "worklog", null, Minutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return change.Build(L.T($"{issue.Key}: work logged"));
    }
}

/// <summary>Changes logged work (time, start, note); the remaining estimate stays.</summary>
public sealed record EditWorkLog(string IssueId, string WorkLogId, int Minutes, DateTimeOffset Started, string Note) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException(L.T("The issue doesn't exist any more."));
        var log = issue.Time.Log.FirstOrDefault(w => w.Id == WorkLogId) ?? throw new BoardRuleException(L.T("That work log doesn't exist any more."));
        if (Minutes <= 0)
            throw new BoardRuleException(L.T("The time spent must be more than zero."));
        var edited = log with { Minutes = Minutes, Started = Started, Note = Note.Trim() };
        var change = new ChangeBuilder(board, context);
        change.WriteIssue(issue, issue with { Time = issue.Time with { Log = issue.Time.Log.Select(w => w.Id == WorkLogId ? edited : w).ToList() } });
        return change.Build(L.T($"{issue.Key}: work log edited"));
    }
}

/// <summary>Deletes logged work; its time goes back onto the remaining estimate.</summary>
public sealed record DeleteWorkLog(string IssueId, string WorkLogId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException(L.T("The issue doesn't exist any more."));
        var log = issue.Time.Log.FirstOrDefault(w => w.Id == WorkLogId) ?? throw new BoardRuleException(L.T("That work log doesn't exist any more."));
        var time = issue.Time with
        {
            Log = issue.Time.Log.Where(w => w.Id != WorkLogId).ToList(),
            RemainingMinutes = issue.Time.RemainingMinutes + log.Minutes,
        };
        var change = new ChangeBuilder(board, context);
        change.WriteIssue(issue, issue with { Time = time });
        change.AddHistory(issue.Id, "worklog", log.Minutes.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
        return change.Build(L.T($"{issue.Key}: work log deleted"));
    }
}

/// <summary>Attaches a file (up to <see cref="MaxBytes"/>).</summary>
public sealed record AddAttachment(string IssueId, string FileName, byte[] Bytes) : IBoardOperation
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public string? Id { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException(L.T("The issue doesn't exist any more."));
        if (Bytes.Length > MaxBytes)
            throw new BoardRuleException(L.T($"Attachments can be up to {MaxBytes / 1024 / 1024} MB."));
        var name = BoardPaths.SafeFileName(Path.GetFileName(FileName));
        var change = new ChangeBuilder(board, context);
        var attachment = new Attachment { Id = Id ?? change.NewId(), FileName = name, Size = Bytes.Length, Author = context.Who, Added = context.Now };
        change.WriteBytes(BoardPaths.Attachment(issue.Id, attachment.Id, name), Bytes);
        change.WriteIssue(issue, issue with { Attachments = [.. issue.Attachments, attachment] });
        change.AddHistory(issue.Id, "attachment", null, name);
        return change.Build($"{issue.Key}: {name} attached");
    }
}

public sealed record RemoveAttachment(string IssueId, string AttachmentId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId) ?? throw new BoardRuleException(L.T("The issue doesn't exist any more."));
        var attachment = issue.Attachments.FirstOrDefault(a => a.Id == AttachmentId)
                         ?? throw new BoardRuleException(L.T("The attachment doesn't exist any more."));
        var change = new ChangeBuilder(board, context);
        change.Delete(BoardPaths.Attachment(issue.Id, attachment.Id, attachment.FileName));
        change.WriteIssue(issue, issue with { Attachments = issue.Attachments.Where(a => a.Id != AttachmentId).ToList() });
        change.AddHistory(issue.Id, "attachment", attachment.FileName, null);
        return change.Build($"{issue.Key}: {attachment.FileName} removed");
    }
}

/// <summary>Adds someone to the board's people.</summary>
public sealed record AddPerson(string Name, string? Email = null) : IBoardOperation
{
    public string? Id { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new BoardRuleException(L.T("A name is needed."));
        if (new BoardUser(Name.Trim(), Email).FindIn(board.Project) is { } existing)
            throw new BoardRuleException(L.T($"{existing.Name} is already on this board."));
        var change = new ChangeBuilder(board, context);
        var person = new BoardPerson { Id = Id ?? change.NewId(), Name = Name.Trim(), Email = string.IsNullOrWhiteSpace(Email) ? null : Email.Trim() };
        change.WriteProject(board.Project with { People = [.. board.Project.People, person] });
        return change.Build(L.T($"{person.Name} added to the board"));
    }
}
