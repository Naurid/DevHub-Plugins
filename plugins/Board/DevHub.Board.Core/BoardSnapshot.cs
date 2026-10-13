using System.Collections.Immutable;
using System.Text.Json;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;

namespace DevHub.Board.Core;

/// <summary>
/// A board as loaded: every file (raw, so plugins like Board.Scrum read their own) and the parsed project,
/// workflows, boards, versions and issues. Immutable: a change makes a new snapshot (<see cref="Apply"/>), so
/// views can read one while the next is being written.
/// </summary>
public sealed class BoardSnapshot
{
    private ILookup<string, Issue>? _children;
    private Dictionary<string, Issue>? _byKey;

    private BoardSnapshot(
        string version,
        ImmutableDictionary<string, byte[]> files,
        BoardProject project,
        ImmutableDictionary<string, Issue> issues,
        ImmutableDictionary<string, Workflow> workflows,
        ImmutableDictionary<string, BoardDefinition> boards,
        ImmutableDictionary<string, FixVersion> versions,
        ImmutableDictionary<string, string> problems)
    {
        Version = version;
        Files = files;
        Project = project;
        Issues = issues;
        Workflows = workflows;
        Boards = boards;
        Versions = versions;
        Problems = problems;
    }

    /// <summary>The store's version these files are at.</summary>
    public string Version { get; }

    /// <summary>Every file, path → bytes.</summary>
    public ImmutableDictionary<string, byte[]> Files { get; }

    public BoardProject Project { get; }

    /// <summary>Issues by id.</summary>
    public ImmutableDictionary<string, Issue> Issues { get; }

    public ImmutableDictionary<string, Workflow> Workflows { get; }
    public ImmutableDictionary<string, BoardDefinition> Boards { get; }
    public ImmutableDictionary<string, FixVersion> Versions { get; }

    /// <summary>Files that couldn't be read (path → why); the board still loads without them.</summary>
    public ImmutableDictionary<string, string> Problems { get; }

    /// <summary>Parses a store's files.</summary>
    /// <exception cref="BoardStoreException">board.json is missing or unreadable.</exception>
    public static BoardSnapshot Load(BoardFiles files)
    {
        var all = files.Files.ToImmutableDictionary(StringComparer.Ordinal);
        if (!all.TryGetValue(BoardPaths.Project, out var projectBytes))
            throw new BoardStoreException("This folder holds no board (board.json is missing).");

        BoardProject project;
        try
        {
            project = BoardJson.Deserialize<BoardProject>(projectBytes);
        }
        catch (JsonException e)
        {
            throw new BoardStoreException("board.json can't be read: " + e.Message, e);
        }

        var empty = new BoardSnapshot(files.Version, all, project,
            ImmutableDictionary.Create<string, Issue>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, Workflow>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, BoardDefinition>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, FixVersion>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, string>(StringComparer.Ordinal));
        return empty.Parse(all.Keys, files.Version, all, project);
    }

    /// <summary>The snapshot after <paramref name="change"/> was written as <paramref name="newVersion"/>.</summary>
    public BoardSnapshot Apply(BoardChange change, string newVersion)
    {
        var files = Files.ToBuilder();
        foreach (var (path, bytes) in change.Files)
        {
            if (bytes is null)
                files.Remove(path);
            else
                files[path] = bytes;
        }
        var all = files.ToImmutable();

        var project = Project;
        if (change.Files.TryGetValue(BoardPaths.Project, out var projectBytes) && projectBytes is not null)
            project = BoardJson.Deserialize<BoardProject>(projectBytes);
        return Parse(change.Files.Keys, newVersion, all, project);
    }

    // Re-reads the given paths into a copy of this snapshot.
    private BoardSnapshot Parse(IEnumerable<string> paths, string version, ImmutableDictionary<string, byte[]> all, BoardProject project)
    {
        var issues = Issues.ToBuilder();
        var workflows = Workflows.ToBuilder();
        var boards = Boards.ToBuilder();
        var versions = Versions.ToBuilder();
        var problems = Problems.ToBuilder();

        foreach (var path in paths)
        {
            problems.Remove(path);
            all.TryGetValue(path, out var bytes);
            if (BoardPaths.IdOf(path, BoardPaths.IssuesFolder, ".json") is { } issueId)
                Read(issues, issueId, path, bytes, (Issue i) => i.Id == issueId ? null : "its id doesn't match the file name");
            else if (BoardPaths.IdOf(path, BoardPaths.WorkflowsFolder, ".json") is { } workflowId)
                Read(workflows, workflowId, path, bytes, (Workflow w) => w.Id == workflowId ? null : "its id doesn't match the file name");
            else if (BoardPaths.IdOf(path, BoardPaths.BoardsFolder, ".json") is { } boardId)
                Read(boards, boardId, path, bytes, (BoardDefinition b) => b.Id == boardId ? null : "its id doesn't match the file name");
            else if (BoardPaths.IdOf(path, BoardPaths.VersionsFolder, ".json") is { } versionId)
                Read(versions, versionId, path, bytes, (FixVersion v) => v.Id == versionId ? null : "its id doesn't match the file name");
        }

        return new BoardSnapshot(version, all, project, issues.ToImmutable(), workflows.ToImmutable(), boards.ToImmutable(),
            versions.ToImmutable(), problems.ToImmutable());

        void Read<T>(ImmutableDictionary<string, T>.Builder target, string id, string path, byte[]? bytes, Func<T, string?> check)
        {
            target.Remove(id);
            if (bytes is null)
                return;
            try
            {
                var item = BoardJson.Deserialize<T>(bytes);
                if (check(item) is { } problem)
                    problems[path] = problem;
                else
                    target[id] = item;
            }
            catch (JsonException e)
            {
                problems[path] = e.Message;
            }
        }
    }

    // ---- Lookups --------------------------------------------------------------------------------------------

    public Issue? Issue(string? id) => id is not null && Issues.TryGetValue(id, out var issue) ? issue : null;

    /// <summary>The issue with this key ("DEV-12", also an old key after a renumbering), case-insensitive.</summary>
    public Issue? IssueByKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        _byKey ??= BuildKeyIndex();
        return _byKey.GetValueOrDefault(key.ToUpperInvariant());
    }

    /// <summary>The issues whose parent is <paramref name="id"/> (an epic's issues, an issue's sub-tasks), by rank.</summary>
    public IReadOnlyList<Issue> ChildrenOf(string id)
    {
        _children ??= Issues.Values.Where(i => i.Parent is not null).OrderBy(i => i.Rank, StringComparer.Ordinal)
            .ToLookup(i => i.Parent!, StringComparer.Ordinal);
        return _children[id].ToList();
    }

    /// <summary>The workflow an issue type follows: its own, else the first one.</summary>
    public Workflow? WorkflowFor(string? issueTypeId)
    {
        var id = Project.IssueType(issueTypeId)?.Workflow;
        if (id is not null && Workflows.TryGetValue(id, out var own))
            return own;
        return Workflows.Values.OrderBy(w => w.Name, StringComparer.Ordinal).ThenBy(w => w.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    public StatusCategory CategoryOf(Issue issue) => Project.Status(issue.Status)?.Category ?? StatusCategory.ToDo;

    public bool IsDone(Issue issue) => CategoryOf(issue) == StatusCategory.Done;

    /// <summary>The level of an issue's type (epic, standard, sub-task).</summary>
    public IssueTypeLevel LevelOf(Issue issue) => Project.IssueType(issue.Type)?.Level ?? IssueTypeLevel.Standard;

    /// <summary>The epic an issue belongs to (a sub-task's through its parent), or null.</summary>
    public Issue? EpicOf(Issue issue)
    {
        var parent = Issue(issue.Parent);
        if (parent is null)
            return null;
        return LevelOf(parent) == IssueTypeLevel.Epic ? parent : EpicOf(parent);
    }

    /// <summary>The issue's change log, oldest first.</summary>
    public IReadOnlyList<HistoryEntry> HistoryOf(string issueId) =>
        BoardJson.ReadLines<HistoryEntry>(Files.GetValueOrDefault(BoardPaths.History(issueId)));

    /// <summary>Links pointing at <paramref name="issueId"/> from other issues ("is blocked by" side).</summary>
    public IEnumerable<(Issue From, IssueLink Link)> IncomingLinks(string issueId) =>
        Issues.Values.SelectMany(i => i.Links.Where(l => l.Target == issueId).Select(l => (i, l)));

    /// <summary>The bytes of an attachment, or null when the file is missing.</summary>
    public byte[]? AttachmentBytes(Issue issue, Attachment attachment) =>
        Files.GetValueOrDefault(BoardPaths.Attachment(issue.Id, attachment.Id, attachment.FileName));

    /// <summary>Files in a folder of the store ("sprints/"), for plugins that keep their own files.</summary>
    public IEnumerable<KeyValuePair<string, byte[]>> FilesIn(string folder) =>
        Files.Where(f => f.Key.StartsWith(folder, StringComparison.Ordinal));

    private Dictionary<string, Issue> BuildKeyIndex()
    {
        var index = new Dictionary<string, Issue>(StringComparer.Ordinal);
        foreach (var issue in Issues.Values)
        {
            foreach (var old in issue.OldKeys)
                index.TryAdd(old.ToUpperInvariant(), issue);
        }
        // Current keys win over old ones.
        foreach (var issue in Issues.Values)
            index[issue.Key.ToUpperInvariant()] = issue;
        return index;
    }
}
