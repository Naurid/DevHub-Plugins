using System.Collections.Concurrent;

namespace DevHub.Git.Core;

/// <summary>What a burst of file changes touched (<see cref="RepositoryWatcher.Changed"/>).</summary>
[Flags]
public enum RepositoryChange
{
    None = 0,

    /// <summary>Working-tree files or the index: what <c>git status</c> shows.</summary>
    Files = 1,

    /// <summary>HEAD, branches, tags, stashes, remotes, a merge / rebase starting or ending, submodules.</summary>
    Refs = 2,

    /// <summary>A <c>.gitignore</c> (or <c>.git/info/exclude</c>): the ignored folders are worth asking git again.</summary>
    Ignores = 4,
}

public sealed class RepositoryChangedEventArgs(RepositoryChange change) : EventArgs
{
    public RepositoryChange Change { get; } = change;
}

/// <summary>
/// Notices when anything in a repository changes (files edited, git commands run elsewhere)
/// and raises <see cref="Changed"/> once per burst, saying what kind of change it was, so a repository tab
/// refreshes only what can have changed.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Debounced: saving a file, or a build touching hundreds, gives one event after things go
/// quiet for <c>debounce</c>, not hundreds.</item>
/// <item>Files git ignores don't count (<see cref="SetIgnored"/>): a build writing <c>bin/</c> and <c>obj/</c>,
/// <c>node_modules/</c> or Unity's <c>Library/</c> changes nothing <c>git status</c> shows.</item>
/// <item>Inside <c>.git</c> only the files that change what DevHub shows count (index, HEAD,
/// refs, merge/rebase markers); objects, logs and lock files are ignored. DevHub's own status
/// calls use GIT_OPTIONAL_LOCKS=0 and don't rewrite the index, so refreshing can't trigger itself.</item>
/// <item><see cref="Changed"/> is raised on a background thread.</item>
/// </list>
/// </remarks>
public sealed class RepositoryWatcher : IDisposable
{
    private static readonly string[] StatusGitFiles = ["index"];
    private static readonly string[] RefGitFiles = ["HEAD", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "ORIG_HEAD", "packed-refs"];

    // The ignored folders of each repository, shared by its watchers (the repository tab's and its diff tabs').
    private static readonly ConcurrentDictionary<string, IgnoredPaths> Ignored = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _root;
    private readonly TimeSpan _debounce;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private int _pending;   // RepositoryChange flags since the last Changed
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
        // Buffer overflow = we missed events; safest is to refresh everything.
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Something relevant changed (debounced; raised on a background thread).</summary>
    public event EventHandler<RepositoryChangedEventArgs>? Changed;

    /// <summary>
    /// The paths git ignores in the repository at <paramref name="repositoryRoot"/>, relative and with "/" (folders end
    /// in "/"), as <c>git ls-files --others --ignored --exclude-standard --directory</c> lists them. Every watcher of
    /// that repository skips changes under them.
    /// </summary>
    public static void SetIgnored(string repositoryRoot, IEnumerable<string> ignored) =>
        Ignored[Path.GetFullPath(repositoryRoot)] = new IgnoredPaths(ignored);

    /// <summary>What a change at <paramref name="relativePath"/> (relative to the repository root) can affect.</summary>
    public static RepositoryChange Classify(string relativePath, IgnoredPaths? ignored = null)
    {
        var path = relativePath.Replace('\\', '/');
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return RepositoryChange.None;
        if (!parts[0].Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            if (parts[^1].Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
                return RepositoryChange.Files | RepositoryChange.Ignores;
            if (path.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase))
                return RepositoryChange.Files | RepositoryChange.Refs;
            return ignored?.Contains(path) == true ? RepositoryChange.None : RepositoryChange.Files;
        }

        if (parts.Length == 1)
            return RepositoryChange.None; // the .git folder itself
        var inside = parts[1];
        if (StatusGitFiles.Contains(inside))
            return RepositoryChange.Files;
        // A merge or rebase starting or ending changes the banner and the files' state too.
        if (RefGitFiles.Contains(inside) || inside.StartsWith("rebase-", StringComparison.Ordinal))
            return RepositoryChange.Refs | RepositoryChange.Files;
        if (inside == "refs")
            return RepositoryChange.Refs;
        if (inside == "info" && parts.Length > 2 && parts[2] == "exclude")
            return RepositoryChange.Files | RepositoryChange.Ignores;
        return RepositoryChange.None;
    }

    /// <summary>Whether a change at <paramref name="relativePath"/> matters at all.</summary>
    public static bool IsRelevant(string relativePath) => Classify(relativePath) != RepositoryChange.None;

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        var change = Classify(Path.GetRelativePath(_root, e.FullPath), Ignored.GetValueOrDefault(_root));
        if (e is RenamedEventArgs renamed)
            change |= Classify(Path.GetRelativePath(_root, renamed.OldFullPath), Ignored.GetValueOrDefault(_root));
        if (change != RepositoryChange.None)
            Restart(change);
    }

    private void OnError(object sender, ErrorEventArgs e) => Restart(RepositoryChange.Files | RepositoryChange.Refs);

    private void Restart(RepositoryChange change)
    {
        if (_disposed)
            return;
        int seen, merged;
        do
        {
            seen = _pending;
            merged = seen | (int)change;
        } while (Interlocked.CompareExchange(ref _pending, merged, seen) != seen);
        _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    private void Fire()
    {
        var change = (RepositoryChange)Interlocked.Exchange(ref _pending, 0);
        if (!_disposed && change != RepositoryChange.None)
            Changed?.Invoke(this, new RepositoryChangedEventArgs(change));
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

/// <summary>Paths git ignores: folders (ending in "/", everything under them) and single files.</summary>
public sealed class IgnoredPaths
{
    private readonly string[] _folders;
    private readonly HashSet<string> _files;

    public IgnoredPaths(IEnumerable<string> paths)
    {
        var all = paths.Select(p => p.Replace('\\', '/').TrimStart('/')).Where(p => p.Length > 0).ToList();
        _folders = all.Where(p => p.EndsWith('/')).ToArray();
        _files = all.Where(p => !p.EndsWith('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool Contains(string relativePath)
    {
        if (_files.Contains(relativePath))
            return true;
        foreach (var folder in _folders)
            if (relativePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                || relativePath.Length + 1 == folder.Length && folder.StartsWith(relativePath, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
