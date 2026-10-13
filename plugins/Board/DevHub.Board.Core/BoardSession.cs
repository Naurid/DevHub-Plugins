using DevHub.Board.Core.Storage;

namespace DevHub.Board.Core;

/// <summary>Why <see cref="BoardSession.Changed"/> fired.</summary>
public sealed class BoardChangedEventArgs(BoardSnapshot snapshot, BoardChange? change) : EventArgs
{
    public BoardSnapshot Snapshot { get; } = snapshot;

    /// <summary>The change this window made, or null when the board was reloaded (changed somewhere else).</summary>
    public BoardChange? Change { get; } = change;
}

/// <summary>
/// An open board: its store, the current snapshot, and the one way to change it (<see cref="ApplyAsync"/>).
/// Writes are queued one at a time; a write refused because the store moved (another window, a sync) reloads
/// and runs the operation again on the fresh snapshot, so nobody's change is overwritten.
/// </summary>
public sealed class BoardSession : IDisposable
{
    private const int MaxAttempts = 3;
    private const int MaxUndo = 50;

    private readonly IBoardStore _store;
    private readonly BoardUser _user;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly Stack<UndoStep> _undo = new();
    private bool _disposed;

    private BoardSession(IBoardStore store, BoardSnapshot snapshot, BoardUser user, TimeProvider time)
    {
        _store = store;
        Snapshot = snapshot;
        _user = user;
        _time = time;
        _store.ChangedOutside += OnChangedOutside;
    }

    public BoardSnapshot Snapshot { get; private set; }

    public IBoardStore Store => _store;

    /// <summary>The person on this board who is you, or null until your first change adds you.</summary>
    public string? CurrentPersonId => _user.FindIn(Snapshot.Project)?.Id;

    /// <summary>Raised after every change and reload (on any thread: a view marshals to its UI thread).</summary>
    public event EventHandler<BoardChangedEventArgs>? Changed;

    /// <summary>Makes ids for new items; tests replace it to get predictable ones.</summary>
    public Func<string> NewId { get; set; } = Model.BoardIds.New;

    /// <summary>Opens the board in <paramref name="store"/>, or returns null when there's none.</summary>
    public static async Task<BoardSession?> OpenAsync(IBoardStore store, BoardUser user, TimeProvider? time = null,
        CancellationToken ct = default)
    {
        var files = await store.LoadAsync(ct);
        return files is null ? null : new BoardSession(store, BoardSnapshot.Load(files), user, time ?? TimeProvider.System);
    }

    /// <summary>Creates a board in an empty store and opens it.</summary>
    /// <exception cref="BoardRuleException">The key or name isn't valid.</exception>
    /// <exception cref="BoardConflictException">The store already holds a board.</exception>
    public static async Task<BoardSession> CreateAsync(IBoardStore store, CreateBoard create, TimeProvider? time = null,
        Func<string>? newId = null, CancellationToken ct = default)
    {
        time ??= TimeProvider.System;
        var context = new OperationContext(null, time.GetUtcNow()) { NewId = newId ?? Model.BoardIds.New };
        var change = create.Build(context);
        var version = await store.WriteAsync(change, BoardFiles.NoBoard, ct);
        var files = await store.LoadAsync(ct) ?? throw new BoardStoreException("The new board couldn't be read back.");
        return new BoardSession(store, BoardSnapshot.Load(files with { Version = version }), create.Creator, time)
        {
            NewId = newId ?? Model.BoardIds.New,
        };
    }

    /// <summary>
    /// Checks and writes <paramref name="operation"/>; returns the written change (empty when nothing changed).
    /// </summary>
    /// <exception cref="BoardRuleException">The board's rules refuse it (nothing was written).</exception>
    public async Task<BoardChange> ApplyAsync(IBoardOperation operation, CancellationToken ct = default)
    {
        await _queue.WaitAsync(ct);
        try
        {
            await EnsureCurrentUserAsync(ct);
            for (var attempt = 1; ; attempt++)
            {
                var before = Snapshot;
                var change = operation.Apply(before, Context());
                if (change.IsEmpty)
                    return change;
                try
                {
                    var version = await _store.WriteAsync(change, before.Version, ct);
                    Snapshot = before.Apply(change, version);
                    PushUndo(before, change);
                    Changed?.Invoke(this, new BoardChangedEventArgs(Snapshot, change));
                    return change;
                }
                catch (BoardConflictException) when (attempt < MaxAttempts)
                {
                    await ReloadCoreAsync(ct);   // and run the operation again on what's there now
                }
            }
        }
        finally
        {
            _queue.Release();
        }
    }

    /// <summary>Reads the store again (after a change somewhere else).</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _queue.WaitAsync(ct);
        try
        {
            await ReloadCoreAsync(ct);
        }
        finally
        {
            _queue.Release();
        }
    }

    /// <summary>What <see cref="UndoAsync"/> would undo, or null.</summary>
    public string? UndoDescription => _undo.TryPeek(out var step) ? step.Message : null;

    /// <summary>
    /// Undoes this window's last change by writing back the files as they were, if nothing changed them since.
    /// </summary>
    /// <exception cref="BoardRuleException">Nothing to undo, or the files were changed since.</exception>
    public async Task UndoAsync(CancellationToken ct = default)
    {
        await _queue.WaitAsync(ct);
        try
        {
            if (!_undo.TryPop(out var step))
                throw new BoardRuleException("There's nothing to undo.");
            foreach (var (path, written) in step.Written)
            {
                var now = Snapshot.Files.GetValueOrDefault(path);
                if (!(now is null && written is null) && (now is null || written is null || !now.AsSpan().SequenceEqual(written)))
                {
                    _undo.Clear();
                    throw new BoardRuleException("It can't be undone: the board changed since.");
                }
            }
            var change = new BoardChange("Undo: " + step.Message, step.Previous);
            var version = await _store.WriteAsync(change, Snapshot.Version, ct);
            Snapshot = Snapshot.Apply(change, version);
            Changed?.Invoke(this, new BoardChangedEventArgs(Snapshot, change));
        }
        finally
        {
            _queue.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _store.ChangedOutside -= OnChangedOutside;
        _store.Dispose();
    }

    private OperationContext Context() => new(CurrentPersonId, _time.GetUtcNow()) { NewId = NewId };

    // The first change by someone not on the board yet adds them (their own write, before the change).
    private async Task EnsureCurrentUserAsync(CancellationToken ct)
    {
        if (CurrentPersonId is not null)
            return;
        var before = Snapshot;
        var change = new AddPerson(_user.Name, _user.Email).Apply(before, Context());
        var version = await _store.WriteAsync(change, before.Version, ct);
        Snapshot = before.Apply(change, version);
    }

    private async Task ReloadCoreAsync(CancellationToken ct)
    {
        var files = await _store.LoadAsync(ct) ?? throw new BoardStoreException("The board isn't there any more.");
        if (files.Version == Snapshot.Version && files.Files.Count == Snapshot.Files.Count
            && files.Files.All(f => Snapshot.Files.TryGetValue(f.Key, out var mine) && mine.AsSpan().SequenceEqual(f.Value)))
            return;
        Snapshot = BoardSnapshot.Load(files);
        _undo.Clear();
        Changed?.Invoke(this, new BoardChangedEventArgs(Snapshot, null));
    }

    private void PushUndo(BoardSnapshot before, BoardChange change)
    {
        var previous = change.Files.Keys.ToDictionary(p => p, p => before.Files.GetValueOrDefault(p), StringComparer.Ordinal);
        _undo.Push(new UndoStep(change.Message, change.Files, previous));
        if (_undo.Count > MaxUndo)
        {
            var keep = _undo.Take(MaxUndo).Reverse().ToList();
            _undo.Clear();
            foreach (var step in keep)
                _undo.Push(step);
        }
    }

    private async void OnChangedOutside(object? sender, EventArgs e)
    {
        try
        {
            await ReloadAsync();
        }
        catch (Exception)
        {
            // The next change or reload reports it; a background refresh mustn't crash the app.
        }
    }

    private sealed record UndoStep(
        string Message,
        IReadOnlyDictionary<string, byte[]?> Written,
        IReadOnlyDictionary<string, byte[]?> Previous);
}
