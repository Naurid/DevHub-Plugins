namespace DevHub.Board.Core.Storage;

/// <summary>
/// Where a board's files live (docs/BOARD_PLAN.md §3). The rest of Board only sees files and a version, never
/// the place: Board ships the folder store, Board.Git adds the <c>devhub/board</c> branch store.
/// </summary>
/// <remarks>
/// Every write is a compare-and-swap: it names the version it was based on, and the store refuses
/// (<see cref="BoardConflictException"/>) when the files moved meanwhile (another window, a sync, a pull). The
/// caller then reloads and re-applies its change, so nothing is ever silently overwritten.
/// </remarks>
public interface IBoardStore : IDisposable
{
    /// <summary>For people: "C:\Users\…\boards\DEV", "DevHub · devhub/board".</summary>
    string DisplayLocation { get; }

    /// <summary>Every file of the board, or null when there's no board here yet.</summary>
    Task<BoardFiles?> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes <paramref name="change"/> if the store is still at <paramref name="expectedVersion"/>
    /// (<see cref="BoardFiles.NoBoard"/> = create the board) and returns the new version.
    /// </summary>
    /// <exception cref="BoardConflictException">The store isn't at <paramref name="expectedVersion"/> any more.</exception>
    Task<string> WriteAsync(BoardChange change, string expectedVersion, CancellationToken ct = default);

    /// <summary>Something other than this store object changed the files (raised on any thread).</summary>
    event EventHandler? ChangedOutside;
}

/// <summary>A board's files (path → bytes, paths as in <see cref="BoardPaths"/>) and the store's version of them.</summary>
public sealed record BoardFiles(string Version, IReadOnlyDictionary<string, byte[]> Files)
{
    /// <summary>The version of a store without a board: writing with it creates one.</summary>
    public const string NoBoard = "";
}

/// <summary>
/// One change to a board: files to write (bytes) or delete (null), and a message saying what happened
/// ("DEV-12: status To Do → In Progress"; the branch store's commit message).
/// </summary>
public sealed record BoardChange(string Message, IReadOnlyDictionary<string, byte[]?> Files)
{
    public bool IsEmpty => Files.Count == 0;
}

/// <summary>The store moved since the version a write was based on.</summary>
public sealed class BoardConflictException(string message) : Exception(message);

/// <summary>The store can't be used (not a folder, no permission, a corrupt file the store needs…).</summary>
public sealed class BoardStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Offers one kind of store (registered in DI by Board and Board.Git), for New Board's "where to keep it" and
/// for reopening a recent board from its saved location.
/// </summary>
public interface IBoardStoreProvider
{
    /// <summary>Stable id saved with recent boards ("folder", "git-branch").</summary>
    string Kind { get; }

    /// <summary>For people: "Folder", "Repository branch (devhub/board)".</summary>
    string DisplayName { get; }

    /// <summary>Whether this kind of store can live at <paramref name="location"/> (a folder path).</summary>
    Task<bool> CanUseAsync(string location, CancellationToken ct = default);

    /// <summary>The store at <paramref name="location"/> (it may hold no board yet).</summary>
    IBoardStore Open(string location);
}
