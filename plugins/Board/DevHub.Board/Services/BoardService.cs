using DevHub.Board.Core;
using DevHub.Board.Core.Storage;
using DevHub.Sdk.Core.Settings;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Services.Board;

/// <summary>Where a board lives: a store kind ("folder", "git-branch") and a location (a folder path).</summary>
public sealed record BoardLocation(string StoreKind, string Location)
{
    /// <summary>"folder|C:\Boards\DEV": the part of document ids that names the board.</summary>
    public string Key => StoreKind + "|" + Location;

    public static BoardLocation? FromKey(string key)
    {
        var bar = key.IndexOf('|');
        return bar <= 0 || bar == key.Length - 1 ? null : new BoardLocation(key[..bar], key[(bar + 1)..]);
    }

    /// <summary>Boards kept "in DevHub": <c>%APPDATA%\DevHub\boards</c> (or under DEVHUB_DATA_DIR).</summary>
    public static string DefaultBoardsFolder
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("DEVHUB_DATA_DIR") is { Length: > 0 } dataDir
                ? dataDir
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(root, "DevHub", "boards");
        }
    }
}

/// <summary>
/// Opens boards and shares them: every tab of one board (the board, its issue tabs, its settings) uses the same
/// <see cref="BoardContext"/>, released when the last of them closes. Also remembers recent boards.
/// </summary>
public sealed class BoardService(IEnumerable<IBoardStoreProvider> providers, ISettingsStore settings, IStatusService status, IDialogService dialogs)
{
    private readonly Dictionary<string, (BoardContext Context, int Users)> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IReadOnlyList<IBoardStoreProvider> Providers { get; } = providers.ToList();

    public BoardUser User => settings.LoadSection<BoardSettings>().User;

    public IReadOnlyList<RecentBoard> RecentBoards => settings.LoadSection<BoardSettings>().RecentBoards;

    /// <summary>Raised when the list of recent boards changes.</summary>
    public event EventHandler? RecentChanged;

    /// <summary>An open board's issues changed (diagrams refresh the badges of shapes linked to them).</summary>
    public event EventHandler? IssuesChanged;

    internal void RaiseIssuesChanged() => IssuesChanged?.Invoke(this, EventArgs.Empty);

    public IBoardStoreProvider? Provider(string kind) => Providers.FirstOrDefault(p => p.Kind == kind);

    /// <summary>The open board at <paramref name="location"/> (shared), or null when there's no board there.</summary>
    /// <param name="remember">False: a look from elsewhere (diagram links) that leaves the recent boards as they are.</param>
    /// <exception cref="BoardStoreException">The store can't be read.</exception>
    public async Task<BoardContext?> AcquireAsync(BoardLocation location, bool remember = true)
    {
        await _gate.WaitAsync();
        try
        {
            if (_open.TryGetValue(location.Key, out var open))
            {
                _open[location.Key] = (open.Context, open.Users + 1);
                return open.Context;
            }
            var provider = Provider(location.StoreKind)
                           ?? throw new BoardStoreException(L.T($"Boards kept as \"{location.StoreKind}\" need a plugin that's switched off."));
            var store = provider.Open(location.Location);
            BoardSession? session;
            try
            {
                session = await BoardSession.OpenAsync(store, User);
            }
            catch
            {
                store.Dispose();
                throw;
            }
            if (session is null)
            {
                store.Dispose();
                return null;
            }
            var context = new BoardContext(location, session, this, status, dialogs);
            _open[location.Key] = (context, 1);
            if (remember)
                Remember(context);
            return context;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Creates a board at <paramref name="location"/> and opens it (shared like <see cref="AcquireAsync"/>).</summary>
    public async Task<BoardContext> CreateAsync(BoardLocation location, CreateBoard create)
    {
        await _gate.WaitAsync();
        try
        {
            var provider = Provider(location.StoreKind) ?? throw new BoardStoreException(L.T("That kind of board store isn't available."));
            var store = provider.Open(location.Location);
            BoardSession session;
            try
            {
                session = await BoardSession.CreateAsync(store, create);
            }
            catch
            {
                store.Dispose();
                throw;
            }
            var context = new BoardContext(location, session, this, status, dialogs);
            _open[location.Key] = (context, 1);
            Remember(context);
            return context;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One user of a board is done with it; the last one closes it.</summary>
    public void Release(BoardContext context)
    {
        _gate.Wait();
        try
        {
            if (!_open.TryGetValue(context.Location.Key, out var open) || !ReferenceEquals(open.Context, context))
                return;
            if (open.Users > 1)
            {
                _open[context.Location.Key] = (open.Context, open.Users - 1);
                return;
            }
            _open.Remove(context.Location.Key);
        }
        finally
        {
            _gate.Release();
        }
        context.Close();
    }

    /// <summary>Puts a board first among the recent ones (its name and key as they are now).</summary>
    public void Remember(BoardContext context)
    {
        var project = context.Snapshot.Project;
        settings.UpdateSection<BoardSettings>(s =>
            s.Remember(new RecentBoard(context.Location.StoreKind, context.Location.Location, project.Key, project.Name)));
        RecentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Forget(RecentBoard board)
    {
        settings.UpdateSection<BoardSettings>(s => s.RecentBoards.RemoveAll(b => b.SameLocation(board)));
        RecentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The board definition last shown for a location, if it still exists.</summary>
    public string? LastBoardView(BoardLocation location) =>
        settings.LoadSection<BoardSettings>().LastBoardView.GetValueOrDefault(location.Key);

    public void SetLastBoardView(BoardLocation location, string boardId) =>
        settings.UpdateSection<BoardSettings>(s => s.LastBoardView[location.Key] = boardId);

    public bool CompactCards => settings.LoadSection<BoardSettings>().CompactCards;
}
