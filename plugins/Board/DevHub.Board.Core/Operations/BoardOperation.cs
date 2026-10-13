using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;

namespace DevHub.Board.Core;

/// <summary>A change refused by the board's rules, with a sentence for people ("A sub-task needs a parent issue.").</summary>
public sealed class BoardRuleException(string message) : Exception(message);

/// <summary>Who does an operation and when; also hands out new ids.</summary>
public sealed record OperationContext(string? Who, DateTimeOffset Now)
{
    /// <summary>Makes new ids (tests replace it to get predictable ones).</summary>
    public Func<string> NewId { get; init; } = BoardIds.New;
}

/// <summary>
/// One change to a board as a plain argument object (<c>new TransitionIssue(issueId, statusId)</c>). It checks
/// itself against the current snapshot and returns the files to write; the ViewModels, bulk edit, imports and
/// the M10 command tools all change boards this way, so every rule lives in one tested place.
/// </summary>
public interface IBoardOperation
{
    /// <exception cref="BoardRuleException">The change isn't allowed.</exception>
    BoardChange Apply(BoardSnapshot board, OperationContext context);
}

/// <summary>
/// Collects what an operation writes: files, and change-log lines for issues (diffed field by field). Reads go
/// through it too (<see cref="Issue"/>), so an operation touching several issues sees its own earlier changes.
/// </summary>
public sealed class ChangeBuilder(BoardSnapshot board, OperationContext context)
{
    private readonly Dictionary<string, byte[]?> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Issue?> _issues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HistoryEntry>> _history = new(StringComparer.Ordinal);
    private BoardProject? _project;

    public BoardSnapshot Board => board;
    public OperationContext Context => context;

    /// <summary>The project as changed so far.</summary>
    public BoardProject Project => _project ?? board.Project;

    /// <summary>An issue as changed so far (null = doesn't exist or deleted here).</summary>
    public Issue? Issue(string? id) =>
        id is null ? null : _issues.TryGetValue(id, out var changed) ? changed : board.Issue(id);

    /// <summary>All issues as changed so far.</summary>
    public IEnumerable<Issue> Issues =>
        board.Issues.Values.Where(i => !_issues.ContainsKey(i.Id)).Concat(_issues.Values.OfType<Issue>());

    /// <summary>A new id not used by any issue, comment or other item here.</summary>
    public string NewId()
    {
        for (var attempt = 0; ; attempt++)
        {
            var id = context.NewId();
            if (!board.Issues.ContainsKey(id) && !_issues.ContainsKey(id) && !board.Workflows.ContainsKey(id)
                && !board.Boards.ContainsKey(id) && !board.Versions.ContainsKey(id))
                return id;
            if (attempt > 100)
                throw new InvalidOperationException("No free id.");
        }
    }

    public void WriteProject(BoardProject project)
    {
        _project = project;
        _files[BoardPaths.Project] = BoardJson.Serialize(project);
    }

    public void Write<T>(string path, T value) => _files[path] = BoardJson.Serialize(value);

    public void WriteBytes(string path, byte[] bytes) => _files[path] = bytes;

    public void Delete(string path)
    {
        if (board.Files.ContainsKey(path) || _files.ContainsKey(path))
            _files[path] = null;
    }

    /// <summary>
    /// Writes an issue (new when <paramref name="before"/> is null), stamps <see cref="Model.Issue.Updated"/>
    /// and adds a change-log line for each field that changed.
    /// </summary>
    public void WriteIssue(Issue? before, Issue after)
    {
        after = after with { Updated = context.Now };
        if (before is null)
        {
            after = after with { Created = context.Now };
            AddHistory(after.Id, "created", null, after.Key);
        }
        else
        {
            foreach (var field in IssueFields.Tracked)
                AddFieldChange(after.Id, field, IssueFields.Get(before, field).Raw, IssueFields.Get(after, field).Raw);
            foreach (var id in before.Custom.Keys.Union(after.Custom.Keys).Distinct())
                AddFieldChange(after.Id, IssueFields.Custom(id), before.Custom.GetValueOrDefault(id), after.Custom.GetValueOrDefault(id));
        }
        _issues[after.Id] = after;
        _files[BoardPaths.Issue(after.Id)] = BoardJson.Serialize(after);
    }

    /// <summary>Deletes an issue with its change log and attachments.</summary>
    public void DeleteIssue(Issue issue)
    {
        _issues[issue.Id] = null;
        _history.Remove(issue.Id);
        Delete(BoardPaths.Issue(issue.Id));
        Delete(BoardPaths.History(issue.Id));
        foreach (var path in board.FilesIn(BoardPaths.AttachmentsOf(issue.Id)).Select(f => f.Key))
            Delete(path);
    }

    /// <summary>A change-log line not tied to a field ("comment", "worklog", "link", "attachment").</summary>
    public void AddHistory(string issueId, string field, string? from, string? to)
    {
        if (!_history.TryGetValue(issueId, out var lines))
            _history[issueId] = lines = [];
        lines.Add(new HistoryEntry { At = context.Now, Who = context.Who, Field = field, From = from, To = to });
    }

    public BoardChange Build(string message)
    {
        foreach (var (issueId, lines) in _history)
        {
            if (_issues.TryGetValue(issueId, out var issue) && issue is null)
                continue;   // deleted here
            var path = BoardPaths.History(issueId);
            var existing = _files.TryGetValue(path, out var pending) ? pending : board.Files.GetValueOrDefault(path);
            _files[path] = BoardJson.AppendLines(existing, lines);
        }
        return new BoardChange(message, new Dictionary<string, byte[]?>(_files, StringComparer.Ordinal));
    }

    private void AddFieldChange(string issueId, string field, string? from, string? to)
    {
        if (from != to)
            AddHistory(issueId, field, from, to);
    }
}
