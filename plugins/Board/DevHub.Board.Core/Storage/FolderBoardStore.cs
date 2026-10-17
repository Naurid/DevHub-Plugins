using System.Globalization;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Core.Storage;

/// <summary>
/// A board kept as files in a folder (docs/BOARD_PLAN.md §3, "Folder store"). Needs nothing but the disk.
/// </summary>
/// <remarks>
/// <para>The version is a counter in <c>.version</c>. A write takes the lock file <c>.lock</c> (held only while
/// writing), checks the counter, writes each file to a temporary name and renames it over the old one (a crash
/// never leaves a half-written file), bumps the counter and lets go of the lock.</para>
/// <para>A <see cref="FileSystemWatcher"/> notices changes made by anything else (another DevHub window, a sync
/// tool, a <c>git pull</c> of a folder inside a repository) by the counter or the files changing.</para>
/// </remarks>
public sealed class FolderBoardStore : IBoardStore
{
    public const string VersionFile = ".version";
    public const string LockFile = ".lock";
    private const string TempSuffix = ".devhub-tmp";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WatchDelay = TimeSpan.FromMilliseconds(300);

    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private string? _lastKnownVersion;
    private DateTime _lastOwnWrite;
    private bool _disposed;

    public FolderBoardStore(string folder)
    {
        Folder = Path.GetFullPath(folder);
    }

    public string Folder { get; }

    public string DisplayLocation => Folder;

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

    private EventHandler? _changedOutside;

    /// <summary>True when <paramref name="folder"/> holds a board (a <c>board.json</c>).</summary>
    public static bool HasBoard(string folder) => File.Exists(Path.Combine(folder, BoardPaths.Project));

    public async Task<BoardFiles?> LoadAsync(CancellationToken ct = default)
    {
        if (!HasBoard(Folder))
            return null;

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var full in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Folder, full).Replace(Path.DirectorySeparatorChar, '/');
            if (IsStoreFile(relative))
                continue;
            files[relative] = await ReadSharedAsync(full, ct);
        }
        var version = ReadVersion();
        _lastKnownVersion = version;
        return new BoardFiles(version, files);
    }

    public async Task<string> WriteAsync(BoardChange change, string expectedVersion, CancellationToken ct = default)
    {
        foreach (var path in change.Files.Keys)
        {
            if (!BoardPaths.IsSafe(path) || IsStoreFile(path))
                throw new BoardStoreException(L.T($"\"{path}\" isn't a path a board may write."));
        }

        Directory.CreateDirectory(Folder);
        await using var lockHandle = await AcquireLockAsync(ct);

        var current = HasBoard(Folder) ? ReadVersion() : BoardFiles.NoBoard;
        if (current != expectedVersion)
            throw new BoardConflictException(expectedVersion == BoardFiles.NoBoard
                ? L.T($"There's already a board in {Folder}.")
                : L.T("The board was changed somewhere else."));

        var next = (long.TryParse(current, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0) + 1;
        var nextVersion = next.ToString(CultureInfo.InvariantCulture);

        // board.json last when creating: a board only "exists" once everything else is in place.
        foreach (var (path, bytes) in change.Files.OrderBy(f => f.Key == BoardPaths.Project ? 1 : 0))
        {
            var full = FullPath(path);
            if (bytes is null)
            {
                if (File.Exists(full))
                    File.Delete(full);
                DeleteEmptyFolders(Path.GetDirectoryName(full));
            }
            else
            {
                await WriteAtomicallyAsync(full, bytes, ct);
            }
        }
        await WriteAtomicallyAsync(FullPath(VersionFile), System.Text.Encoding.ASCII.GetBytes(nextVersion), ct);

        lock (_gate)
        {
            _lastKnownVersion = nextVersion;
            _lastOwnWrite = DateTime.UtcNow;
        }
        return nextVersion;
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

    /// <summary>Files the store keeps for itself, never part of the board.</summary>
    private static bool IsStoreFile(string relative) =>
        relative is VersionFile or LockFile || relative.EndsWith(TempSuffix, StringComparison.Ordinal);

    private string FullPath(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Folder, relative.Replace('/', Path.DirectorySeparatorChar)));
        var root = Folder.EndsWith(Path.DirectorySeparatorChar) ? Folder : Folder + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new BoardStoreException(L.T($"\"{relative}\" is outside the board folder."));
        return full;
    }

    private string ReadVersion()
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(Folder, VersionFile)).Trim();
            return text.Length > 0 ? text : "0";
        }
        catch (FileNotFoundException)
        {
            return "0";
        }
        catch (DirectoryNotFoundException)
        {
            return "0";
        }
    }

    private static async Task<byte[]> ReadSharedAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, useAsync: true);
        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + TempSuffix;
        await File.WriteAllBytesAsync(temp, bytes, ct);
        File.Move(temp, path, overwrite: true);
    }

    private void DeleteEmptyFolders(string? folder)
    {
        while (folder is not null && folder.Length > Folder.Length
               && folder.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
            folder = Path.GetDirectoryName(folder);
        }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken ct)
    {
        var path = Path.Combine(Folder, LockFile);
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, ct);
            }
            catch (IOException e)
            {
                throw new BoardStoreException(L.T($"The board in {Folder} is locked by another program."), e);
            }
        }
    }

    private void StartWatching()
    {
        if (_watcher is not null || _disposed || !Directory.Exists(Folder))
            return;
        try
        {
            _watcher = new FileSystemWatcher(Folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _watcher = null;   // no live updates; the board still works
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        var name = Path.GetFileName(e.FullPath);
        if (name == LockFile || name.EndsWith(TempSuffix, StringComparison.Ordinal))
            return;
        lock (_gate)
        {
            if (_disposed)
                return;
            _debounce ??= new Timer(_ => CheckForOutsideChange());
            _debounce.Change(WatchDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void CheckForOutsideChange()
    {
        EventHandler? handler;
        lock (_gate)
        {
            if (_disposed)
                return;
            // A different counter = someone else wrote. The same counter but files changed long after our own
            // last write = a tool that edits files without the counter (a sync, a git pull, a text editor).
            var version = HasBoard(Folder) ? ReadVersion() : BoardFiles.NoBoard;
            var changed = version != _lastKnownVersion || DateTime.UtcNow - _lastOwnWrite > WatchDelay * 3;
            if (!changed)
                return;
            handler = _changedOutside;
        }
        handler?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Offers the folder store.</summary>
public sealed class FolderBoardStoreProvider : IBoardStoreProvider
{
    public const string KindId = "folder";

    public string Kind => KindId;
    public string DisplayName => L.T("Folder");

    public Task<bool> CanUseAsync(string location, CancellationToken ct = default) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(location) && !File.Exists(location));

    public IBoardStore Open(string location) => new FolderBoardStore(location);
}
