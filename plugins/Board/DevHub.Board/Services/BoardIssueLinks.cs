using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using DevHub.Integration;
using DevHub.Sdk.Core.Localization;
using DevHub.ViewModels.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace DevHub.Services.Board;

/// <summary>
/// Issues for Diagrams to link shapes to (<see cref="IIssueLinks"/>): searches the recent boards (open ones are
/// shared, closed ones read and closed again), opens an issue in its tab, creates a story on the most recent board.
/// </summary>
public sealed class BoardIssueLinks : IIssueLinks, IDisposable
{
    private const int BoardsSearched = 12;

    private readonly BoardService _boards;
    private readonly IServiceProvider _services;
    private readonly IStatusService _status;

    public BoardIssueLinks(BoardService boards, IServiceProvider services, IStatusService status)
    {
        _boards = boards;
        _services = services;
        _status = status;
        _boards.IssuesChanged += OnIssuesChanged;
    }

    public event EventHandler? Changed;

    private void OnIssuesChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    // Resolved on use: the document service is built from the documents, which need the Board services.
    private IDocumentService Documents => _services.GetRequiredService<IDocumentService>();

    /// <summary>The project folder a board belongs to: the repository of a branch board, the folder holding <c>.devhub/board</c>.</summary>
    public static string? ProjectFolderOf(BoardLocation location)
    {
        string path;
        try
        {
            path = Path.GetFullPath(location.Location);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (location.StoreKind != FolderBoardStoreProvider.KindId)
            return path;
        var folder = new DirectoryInfo(path);
        return folder.Name.Equals("board", StringComparison.OrdinalIgnoreCase) && folder.Parent is { } parent
               && parent.Name.Equals(".devhub", StringComparison.OrdinalIgnoreCase)
            ? parent.Parent?.FullName
            : null;
    }

    private async Task<List<(BoardLocation Location, BoardSnapshot Snapshot)>> SnapshotsAsync(string? projectKey = null)
    {
        var found = new List<(BoardLocation, BoardSnapshot)>();
        foreach (var recent in _boards.RecentBoards.Take(BoardsSearched))
        {
            if (projectKey is not null && !recent.Key.Equals(projectKey, StringComparison.OrdinalIgnoreCase))
                continue;
            var location = new BoardLocation(recent.StoreKind, recent.Location);
            if (_boards.Provider(location.StoreKind) is null)
                continue;
            try
            {
                if (await _boards.AcquireAsync(location, remember: false) is not { } context)
                    continue;
                try
                {
                    found.Add((location, context.Snapshot));
                }
                finally
                {
                    _boards.Release(context);
                }
            }
            catch (Exception e) when (e is BoardStoreException or IOException or UnauthorizedAccessException)
            {
                // A board that moved or can't be read is left out.
            }
        }
        return found;
    }

    private static IssueLinkInfo Info(BoardSnapshot board, Issue issue) => new(issue.Key, issue.Summary,
        board.Project.Status(issue.Status)?.Name ?? issue.Status,
        board.CategoryOf(issue) switch { StatusCategory.Done => "done", StatusCategory.InProgress => "inprogress", _ => "todo" });

    private static string? ProjectKeyOf(string issueKey) => issueKey.LastIndexOf('-') is var dash and > 0 ? issueKey[..dash] : null;

    public async Task<IReadOnlyList<IssueLinkInfo>> SearchAsync(string text, int max = 20)
    {
        var query = text.Trim();
        var found = new List<(IssueLinkInfo Info, int Score, DateTimeOffset Updated)>();
        foreach (var (_, board) in await SnapshotsAsync())
            foreach (var issue in board.Issues.Values)
            {
                var score = query.Length == 0 ? 1
                    : issue.Key.Equals(query, StringComparison.OrdinalIgnoreCase) ? 3
                    : issue.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2
                    : issue.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (score > 0)
                    found.Add((Info(board, issue), score, issue.Updated));
            }
        return found.OrderByDescending(x => x.Score).ThenByDescending(x => x.Updated).Take(max).Select(x => x.Info).ToList();
    }

    public async Task<IssueLinkInfo?> FindAsync(string key)
    {
        foreach (var (_, board) in await SnapshotsAsync(ProjectKeyOf(key)))
            if (board.IssueByKey(key) is { } issue)
                return Info(board, issue);
        return null;
    }

    public void Open(string key) => _ = OpenAsync(key);

    private async Task OpenAsync(string key)
    {
        foreach (var (location, board) in await SnapshotsAsync(ProjectKeyOf(key)))
            if (board.IssueByKey(key) is { } issue)
            {
                Documents.OpenById(IssueDocumentFactory.IdFor(location, issue.Id));
                return;
            }
        _status.Show(L.T($"{key} isn't on any recent board."));
    }

    public async Task<IssueLinkInfo?> CreateStoryAsync(string summary, string description)
    {
        var recent = _boards.RecentBoards.FirstOrDefault(r => _boards.Provider(r.StoreKind) is not null);
        if (recent is null)
        {
            _status.Show(L.T("Open or create a board first: the story goes on the most recent one."));
            return null;
        }
        BoardContext? context;
        try
        {
            context = await _boards.AcquireAsync(new BoardLocation(recent.StoreKind, recent.Location), remember: false);
        }
        catch (Exception e) when (e is BoardStoreException or IOException or UnauthorizedAccessException)
        {
            _status.Show(L.T($"The board couldn't be opened: {e.Message}"));
            return null;
        }
        if (context is null)
            return null;
        try
        {
            var project = context.Project;
            var type = project.IssueTypes.FirstOrDefault(t => t.Id.Equals("story", StringComparison.OrdinalIgnoreCase)
                                                             || t.Name.Equals("Story", StringComparison.OrdinalIgnoreCase)
                                                             || t.Name.Equals(L.T("Story"), StringComparison.OrdinalIgnoreCase))
                       ?? project.IssueTypes.FirstOrDefault(t => t.Level == IssueTypeLevel.Standard);
            if (type is null)
            {
                _status.Show(L.T("The board has no issue type for stories."));
                return null;
            }
            var key = IssueKey.Format(project.Key, project.NextNumber);
            if (!await context.RunAsync(new CreateIssue(type.Id, summary) { Description = description }))
                return null;
            var created = context.Snapshot.IssueByKey(key);
            if (created is null)
                return null;
            _status.Show(L.T($"Created {created.Key} on {project.Name}."));
            return Info(context.Snapshot, created);
        }
        finally
        {
            _boards.Release(context);
        }
    }

    public void Dispose() => _boards.IssuesChanged -= OnIssuesChanged;
}
