using System.Globalization;
using System.Text;
using DevHub.Board.Core;
using DevHub.Board.Core.Storage;
using DevHub.Git.Core;

namespace DevHub.Board.Git.Core;

/// <summary>
/// A board on a repository's <c>devhub/board</c> branch (docs/BOARD_PLAN.md §3, "Branch store"). The branch is never
/// checked out: files are read and written with git plumbing, so the board is the same on every code branch and
/// board changes never mix with code commits. The version is the branch's commit id.
/// </summary>
/// <remarks>
/// <para>Write = <c>hash-object</c> each file, a temporary index (<c>GIT_INDEX_FILE</c> under the git folder, so the
/// real staging area is never used) with <c>read-tree</c> + <c>update-index --index-info</c>, <c>write-tree</c>,
/// <c>commit-tree</c> (parent: the version written on), then <c>update-ref new old</c>: git's own compare-and-swap.</para>
/// <para>Rules: never the working tree, the index or HEAD; git only through <see cref="GitRunner"/> (every command
/// is in the Git Console); a moved branch means "reload", never overwrite.</para>
/// </remarks>
public sealed class BranchBoardStore : IBoardStore
{
    public const string BranchName = "devhub/board";
    public const string Ref = "refs/heads/" + BranchName;
    private const string ZeroId = "0000000000000000000000000000000000000000";
    private static readonly TimeSpan WatchDelay = TimeSpan.FromMilliseconds(400);

    private readonly GitRunner _runner;
    private readonly object _gate = new();
    private string? _gitDir;
    private string? _lastKnown;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private EventHandler? _changedOutside;
    private bool _disposed;

    /// <param name="repository">The repository's folder (its top level, or any folder inside it).</param>
    public BranchBoardStore(GitRunner runner, string repository)
    {
        _runner = runner;
        Repository = Path.GetFullPath(repository);
    }

    public string Repository { get; }

    public string DisplayLocation => $"{Repository} · {BranchName}";

    public event EventHandler? ChangedOutside
    {
        add
        {
            lock (_gate)
            {
                _changedOutside += value;
                StartWatching();
            }
        }
        remove
        {
            lock (_gate)
                _changedOutside -= value;
        }
    }

    /// <summary>True when <paramref name="folder"/> is inside a git repository.</summary>
    public static async Task<bool> IsRepositoryAsync(GitRunner runner, string folder, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder))
            return false;
        var result = await runner.RunAsync(folder, ["rev-parse", "--is-inside-work-tree"], new GitRunOptions { ReadOnly = true, AllowFailure = true }, ct);
        return result.Succeeded && result.StandardOutput.Trim() == "true";
    }

    /// <summary>The commit the branch is at, or null when there's no board branch.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(Repository, ["rev-parse", "--verify", "--quiet", Ref + "^{commit}"],
            new GitRunOptions { ReadOnly = true, AllowFailure = true }, ct);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    public async Task<BoardFiles?> LoadAsync(CancellationToken ct = default)
    {
        var version = await GetVersionAsync(ct);
        lock (_gate)
            _lastKnown = version;
        if (version is null)
            return null;

        // Every blob of the commit's tree: names from ls-tree, contents from one cat-file --batch.
        var tree = await _runner.RunAsync(Repository, ["ls-tree", "-r", "-z", "--full-tree", version], new GitRunOptions { ReadOnly = true }, ct);
        var entries = new List<(string Sha, string Path)>();
        foreach (var record in tree.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> <type> <sha>\t<path>"
            var tab = record.IndexOf('\t');
            var meta = record[..tab].Split(' ');
            if (meta.Length == 3 && meta[1] == "blob")
                entries.Add((meta[2], record[(tab + 1)..]));
        }
        if (!entries.Any(e => e.Path == BoardPaths.Project))
            throw new BoardStoreException($"The {BranchName} branch has no board.json: it isn't a board.");

        var request = Encoding.ASCII.GetBytes(string.Concat(entries.Select(e => e.Sha + "\n")));
        var batch = await _runner.RunBinaryAsync(Repository, ["cat-file", "--batch"], request, new GitRunOptions { ReadOnly = true }, ct);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var output = batch.StandardOutput;
        var position = 0;
        foreach (var (sha, path) in entries)
        {
            // "<sha> blob <size>\n<bytes>\n"
            var lineEnd = Array.IndexOf(output, (byte)'\n', position);
            var header = Encoding.ASCII.GetString(output, position, lineEnd - position).Split(' ');
            if (header.Length != 3 || header[0] != sha)
                throw new BoardStoreException("git cat-file answered something unexpected.");
            var size = int.Parse(header[2], CultureInfo.InvariantCulture);
            files[path] = output.AsSpan(lineEnd + 1, size).ToArray();
            position = lineEnd + 1 + size + 1;
        }
        return new BoardFiles(version, files);
    }

    public async Task<string> WriteAsync(BoardChange change, string expectedVersion, CancellationToken ct = default)
    {
        foreach (var path in change.Files.Keys)
        {
            if (!BoardPaths.IsSafe(path))
                throw new BoardStoreException($"\"{path}\" isn't a path a board may write.");
        }

        var current = await GetVersionAsync(ct);
        if ((current ?? BoardFiles.NoBoard) != expectedVersion)
            throw new BoardConflictException(expectedVersion == BoardFiles.NoBoard
                ? $"The repository already has a {BranchName} branch."
                : "The board was changed somewhere else.");

        var gitDir = await GitDirAsync(ct);
        var index = Path.Combine(gitDir, "devhub", "board-index-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        var env = new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = index };
        var temp = new List<string>();
        try
        {
            // The tree as it is, in a throw-away index.
            await _runner.RunAsync(Repository, current is null ? ["read-tree", "--empty"] : ["read-tree", current],
                new GitRunOptions { Environment = env }, ct);

            // New contents as blobs (one hash-object for all of them, from temporary files).
            var writes = change.Files.Where(f => f.Value is not null).ToList();
            var shas = new List<string>();
            if (writes.Count > 0)
            {
                foreach (var (_, bytes) in writes)
                {
                    var file = Path.Combine(Path.GetDirectoryName(index)!, "blob-" + Guid.NewGuid().ToString("N"));
                    await File.WriteAllBytesAsync(file, bytes!, ct);
                    temp.Add(file);
                }
                var hashed = await _runner.RunAsync(Repository, ["hash-object", "-w", "--no-filters", "--stdin-paths"],
                    new GitRunOptions { StandardInput = string.Join("\n", temp) + "\n" }, ct);
                shas = hashed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (shas.Count != writes.Count)
                    throw new BoardStoreException("git hash-object answered something unexpected.");
            }

            // The changes, in one update-index: "<mode> <sha>\t<path>\0", mode 0 removes.
            var info = new StringBuilder();
            for (var i = 0; i < writes.Count; i++)
                info.Append("100644 ").Append(shas[i]).Append('\t').Append(writes[i].Key).Append('\0');
            foreach (var path in change.Files.Where(f => f.Value is null).Select(f => f.Key))
                info.Append("0 ").Append(ZeroId).Append('\t').Append(path).Append('\0');
            if (info.Length > 0)
                await _runner.RunAsync(Repository, ["update-index", "-z", "--index-info"],
                    new GitRunOptions { Environment = env, StandardInput = info.ToString() }, ct);

            var tree = (await _runner.RunAsync(Repository, ["write-tree"], new GitRunOptions { Environment = env }, ct)).StandardOutput.Trim();
            var commitArgs = new List<string> { "commit-tree", tree };
            if (current is not null)
                commitArgs.AddRange(["-p", current]);
            commitArgs.AddRange(["-F", "-"]);
            var commit = (await _runner.RunAsync(Repository, commitArgs,
                new GitRunOptions { StandardInput = "board: " + change.Message + "\n", Environment = await IdentityAsync(ct) }, ct)).StandardOutput.Trim();

            // Compare-and-swap: only if the branch is still where this write started (or still doesn't exist).
            var swap = await _runner.RunAsync(Repository, ["update-ref", "-m", "board: " + change.Message, Ref, commit, current ?? ZeroId],
                new GitRunOptions { AllowFailure = true }, ct);
            if (!swap.Succeeded)
                throw new BoardConflictException("The board was changed somewhere else.");

            lock (_gate)
                _lastKnown = commit;
            return commit;
        }
        finally
        {
            TryDelete(index);
            TryDelete(index + ".lock");
            foreach (var file in temp)
                TryDelete(file);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    private async Task<string> GitDirAsync(CancellationToken ct)
    {
        if (_gitDir is not null)
            return _gitDir;
        var result = await _runner.RunAsync(Repository, ["rev-parse", "--absolute-git-dir"], new GitRunOptions { ReadOnly = true }, ct);
        return _gitDir = result.StandardOutput.Trim();
    }

    // commit-tree needs an author: the repository's, else a neutral one (a fresh repository without user.name).
    private async Task<IReadOnlyDictionary<string, string?>?> IdentityAsync(CancellationToken ct)
    {
        var name = await _runner.RunAsync(Repository, ["config", "user.name"], new GitRunOptions { ReadOnly = true, AllowFailure = true }, ct);
        if (name.Succeeded && name.StandardOutput.Trim().Length > 0)
            return null;
        return new Dictionary<string, string?>
        {
            ["GIT_AUTHOR_NAME"] = "DevHub Board",
            ["GIT_AUTHOR_EMAIL"] = "board@devhub.local",
            ["GIT_COMMITTER_NAME"] = "DevHub Board",
            ["GIT_COMMITTER_EMAIL"] = "board@devhub.local",
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // The branch can move without DevHub (a pull, a push from another DevHub on this machine, git in a terminal):
    // watch the git folder and compare the branch's commit after a quiet moment.
    // Started in the background: finding the git folder runs git, and the caller may be the UI thread (waiting on it
    // there would deadlock with git's own continuation).
    private void StartWatching()
    {
        if (_watching || _disposed)
            return;
        _watching = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var gitDir = await GitDirAsync(CancellationToken.None).ConfigureAwait(false);
                var watcher = new FileSystemWatcher(gitDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                };
                watcher.Changed += OnGitFolderEvent;
                watcher.Created += OnGitFolderEvent;
                watcher.Renamed += OnGitFolderEvent;
                watcher.Deleted += OnGitFolderEvent;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        watcher.Dispose();
                        return;
                    }
                    _watcher = watcher;
                    watcher.EnableRaisingEvents = true;
                }
            }
            catch (Exception e) when (e is IOException or ArgumentException or GitException or UnauthorizedAccessException)
            {
                // No live updates; the board still works.
            }
        });
    }

    private bool _watching;

    private void OnGitFolderEvent(object sender, FileSystemEventArgs e)
    {
        var path = e.FullPath.Replace('\\', '/');
        if (!path.Contains("refs/heads/devhub", StringComparison.Ordinal) && !path.EndsWith("packed-refs", StringComparison.Ordinal))
            return;
        lock (_gate)
        {
            if (_disposed)
                return;
            _debounce ??= new Timer(_ => _ = CheckForOutsideChangeAsync());
            _debounce.Change(WatchDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task CheckForOutsideChangeAsync()
    {
        try
        {
            var version = await GetVersionAsync();
            EventHandler? handler;
            lock (_gate)
            {
                if (_disposed || version == _lastKnown)
                    return;
                handler = _changedOutside;
            }
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) when (e is GitException or IOException)
        {
        }
    }
}

/// <summary>Offers the branch store (registered by Board.Git when git is installed).</summary>
public sealed class BranchBoardStoreProvider(GitRunner runner) : IBoardStoreProvider
{
    public const string KindId = "git-branch";

    public string Kind => KindId;
    public string DisplayName => "On the repository's devhub/board branch";

    public Task<bool> CanUseAsync(string location, CancellationToken ct = default) => BranchBoardStore.IsRepositoryAsync(runner, location, ct);

    public IBoardStore Open(string location) => new BranchBoardStore(runner, location);
}
