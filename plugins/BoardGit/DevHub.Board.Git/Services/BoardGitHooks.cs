using System.Collections.Concurrent;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Board.Git.Core;
using DevHub.Git.Core;
using DevHub.Integration;

namespace DevHub.Services.BoardGit;

/// <summary>Suggests "DEV-12: " in Git's commit box on a branch named after an issue of one of the repository's boards.</summary>
public sealed class BoardCommitSuggester(BoardGitServices services) : ICommitMessageSuggester
{
    public async Task<string?> SuggestAsync(CommitContext context, CancellationToken ct)
    {
        if (!services.Settings.SuggestCommitMessage || context.Branch is null)
            return null;
        foreach (var board in await services.BoardsOfRepositoryAsync(context.RepositoryFolder, ct))
        {
            if (IssueKeys.Find(context.Branch, board.Key).FirstOrDefault() is { } key)
                return key + ": ";
        }
        return null;
    }
}

/// <summary>
/// Marks in Git's commit list and branch tree: the issues a commit message or branch name names, with their status
/// (✓ when done). Answers from a cache filled in the background per repository (git log of the project's keys, the
/// board's issues), refreshed after commits and fetches; <see cref="Changed"/> asks Git to look again.
/// </summary>
public sealed class BoardGitDecorator : IGitDecorator, IDisposable
{
    private readonly BoardGitServices _services;
    private IRepositoryEvents? _events;
    private readonly ConcurrentDictionary<string, RepoCache> _caches = new(StringComparer.OrdinalIgnoreCase);

    private sealed record IssueInfo(string Key, string Summary, string Status, bool Done);

    private sealed class RepoCache
    {
        public Dictionary<string, IReadOnlyList<string>> CommitKeys { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, IssueInfo> Issues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> ProjectKeys { get; set; } = [];
        public bool Loading { get; set; }
        public DateTime LoadedAt { get; set; }
    }

    public BoardGitDecorator(BoardGitServices services, IServiceProvider provider)
    {
        _services = services;
        _provider = provider;
    }

    private readonly IServiceProvider _provider;

    // Git's events are looked up on first use, not in the constructor: Git builds its tabs (which ask for
    // decorators) while the services those events come from are still being built (docs/UI_GUIDE.md §8).
    private void AttachEvents()
    {
        if (_attached)
            return;
        _attached = true;
        _events = (IRepositoryEvents?)_provider.GetService(typeof(IRepositoryEvents));
        if (_events is not null)
        {
            _events.Committed += OnRepositoryChanged;
            _events.Fetched += OnRepositoryChanged;
        }
    }

    private bool _attached;

    public event EventHandler<DecorationsChangedEventArgs>? Changed;

    public IReadOnlyDictionary<string, IReadOnlyList<Decoration>> DecorateCommits(string repositoryFolder, IReadOnlyList<string> commitShas)
    {
        var cache = CacheFor(repositoryFolder);
        var result = new Dictionary<string, IReadOnlyList<Decoration>>(StringComparer.Ordinal);
        foreach (var sha in commitShas)
        {
            if (cache.CommitKeys.TryGetValue(sha, out var keys))
                result[sha] = keys.Select(k => Decorate(k, cache)).ToList();
        }
        return result;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<Decoration>> DecorateBranches(string repositoryFolder, IReadOnlyList<string> branches)
    {
        var cache = CacheFor(repositoryFolder);
        var result = new Dictionary<string, IReadOnlyList<Decoration>>(StringComparer.Ordinal);
        foreach (var branch in branches)
        {
            var keys = cache.ProjectKeys.SelectMany(p => IssueKeys.Find(branch, p)).ToList();
            if (keys.Count > 0)
                result[branch] = keys.Select(k => Decorate(k, cache)).ToList();
        }
        return result;
    }

    private static Decoration Decorate(string key, RepoCache cache) =>
        cache.Issues.TryGetValue(key, out var issue)
            ? new Decoration(issue.Done ? "success" : "edit", issue.Done ? DecorationTone.Success : DecorationTone.Neutral,
                $"{issue.Key} · {issue.Status} · {issue.Summary}")
            : new Decoration("edit", DecorationTone.Neutral, key);

    // The cache as it is; a refresh in the background when it's empty or older than a minute.
    private RepoCache CacheFor(string repository)
    {
        AttachEvents();
        var cache = _caches.GetOrAdd(repository, _ => new RepoCache());
        if (!cache.Loading && DateTime.UtcNow - cache.LoadedAt > TimeSpan.FromMinutes(1))
        {
            cache.Loading = true;
            _ = RefreshAsync(repository, cache);
        }
        return cache;
    }

    private async Task RefreshAsync(string repository, RepoCache cache)
    {
        try
        {
            var boards = await _services.BoardsOfRepositoryAsync(repository);
            var commitKeys = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var issues = new Dictionary<string, IssueInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var board in boards)
            {
                foreach (var commit in await _services.Queries.ProjectCommitsAsync(repository, board.Key))
                {
                    var keys = IssueKeys.Find(commit.Subject, board.Key);
                    commitKeys[commit.Sha] = commitKeys.TryGetValue(commit.Sha, out var before) ? [.. before, .. keys] : keys;
                }
                await _services.WithBoardAsync(board, context =>
                {
                    foreach (var issue in context.Snapshot.Issues.Values)
                        issues[issue.Key] = new IssueInfo(issue.Key, issue.Summary, context.StatusName(issue.Status), context.Snapshot.IsDone(issue));
                    return Task.FromResult(true);
                });
            }
            cache.CommitKeys = commitKeys;
            cache.Issues = issues;
            cache.ProjectKeys = boards.Select(b => b.Key).Distinct().ToList();
            cache.LoadedAt = DateTime.UtcNow;
            if (commitKeys.Count > 0 || boards.Count > 0)
                Changed?.Invoke(this, new DecorationsChangedEventArgs(repository));
        }
        catch (Exception e) when (e is GitException or IOException or InvalidOperationException)
        {
            cache.LoadedAt = DateTime.UtcNow;   // try again in a minute
        }
        finally
        {
            cache.Loading = false;
        }
    }

    private void OnRepositoryChanged(object? sender, RepositoryEventArgs e)
    {
        if (_caches.TryGetValue(e.RepositoryFolder, out var cache))
            cache.LoadedAt = DateTime.MinValue;   // refresh on the next ask
        Changed?.Invoke(this, new DecorationsChangedEventArgs(e.RepositoryFolder));
    }

    public void Dispose()
    {
        if (_events is not null)
        {
            _events.Committed -= OnRepositoryChanged;
            _events.Fetched -= OnRepositoryChanged;
        }
    }
}
