namespace DevHub.Git.Core;

/// <summary>
/// Notices when anything in a repository changes (files edited, git commands run elsewhere)
/// and raises <see cref="Changed"/> once per burst, so a repository tab can refresh its status.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Debounced: saving a file, or a build touching hundreds, gives one event after things go
/// quiet for <c>debounce</c>, not hundreds.</item>
/// <item>Inside <c>.git</c> only the files that change what status shows count (index, HEAD,
/// refs, merge/rebase markers); objects, logs and lock files are ignored. DevHub's own status
/// calls use GIT_OPTIONAL_LOCKS=0 and don't rewrite the index, so refreshing can't trigger itself.</item>
/// <item><see cref="Changed"/> is raised on a background thread.</item>
/// </list>
/// </remarks>
public sealed class RepositoryWatcher : IDisposable
{
    private static readonly string[] RelevantGitFiles =
        ["index", "HEAD", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "ORIG_HEAD", "packed-refs"];

    private readonly string _root;
    private readonly TimeSpan _debounce;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private bool _disposed;

    public RepositoryWatcher(string repositoryRoot, TimeSpan? debounce = null)
    {
        _root = Path.GetFullPath(repositoryRoot);
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                           | NotifyFilters.LastWrite | NotifyFilters.Size,
            // Bigger buffer than the 8 KB default, for bursts like a branch switch.
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += OnFileEvent;
        _watcher.Changed += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        // Buffer overflow = we missed events; safest is to refresh.
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Something relevant changed (debounced; raised on a background thread).</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Whether a change at <paramref name="relativePath"/> (relative to the repository root)
    /// can affect what <c>git status</c> shows.
    /// </summary>
    public static bool IsRelevant(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;
        if (!parts[0].Equals(".git", StringComparison.OrdinalIgnoreCase))
            return true; // working tree

        if (parts.Length == 1)
            return false; // the .git folder itself
        var inside = parts[1];
        return RelevantGitFiles.Contains(inside)
               || inside == "refs"
               || inside.StartsWith("rebase-", StringComparison.Ordinal);
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (IsRelevant(Path.GetRelativePath(_root, e.FullPath)))
            Restart();
    }

    private void OnError(object sender, ErrorEventArgs e) => Restart();

    private void Restart()
    {
        if (!_disposed)
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    private void Fire()
    {
        if (!_disposed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnFileEvent;
        _watcher.Changed -= OnFileEvent;
        _watcher.Deleted -= OnFileEvent;
        _watcher.Renamed -= OnFileEvent;
        _watcher.Error -= OnError;
        _watcher.Dispose();
        _timer.Dispose();
    }
}
