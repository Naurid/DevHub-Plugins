using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Search;
using DevHub.Board.Core.Storage;
using DevHub.ViewModels.Dialogs;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Services.Board;

/// <summary>
/// One open board as the UI sees it: the session, its current snapshot (raised on the UI thread), and
/// <see cref="RunAsync"/>, the one way views change the board (rule refusals go to the status bar, store problems
/// to a dialog). Shared by every tab of the board (<see cref="BoardService"/>).
/// </summary>
public sealed partial class BoardContext : ObservableObject
{
    private readonly BoardService _service;
    private readonly IStatusService _status;
    private readonly IDialogService _dialogs;

    internal BoardContext(BoardLocation location, BoardSession session, BoardService service, IStatusService status, IDialogService dialogs)
    {
        Location = location;
        Session = session;
        _service = service;
        _status = status;
        _dialogs = dialogs;
        _snapshot = session.Snapshot;
        Session.Changed += OnSessionChanged;
    }

    public BoardLocation Location { get; }
    public BoardSession Session { get; }
    public BoardService Service => _service;
    public IDialogService Dialogs => _dialogs;
    public IStatusService Status => _status;

    /// <summary>The board now. Replaced (never changed) after every change or reload, on the UI thread.</summary>
    [ObservableProperty] private BoardSnapshot _snapshot;

    /// <summary>Raised on the UI thread after <see cref="Snapshot"/> changed.</summary>
    public event EventHandler? Changed;

    public BoardProject Project => Snapshot.Project;

    /// <summary>You on this board (null until your first change adds you).</summary>
    public string? Me => Session.CurrentPersonId;

    public string Title => $"{Project.Key} · {Project.Name}";

    public QueryContext QueryContext() => new(Snapshot, Me, DateTimeOffset.Now);

    /// <summary>
    /// Writes an operation. Returns false (and says why in the status bar) when the board's rules refuse it;
    /// a store problem (disk, lock) is shown in a dialog.
    /// </summary>
    public async Task<bool> RunAsync(IBoardOperation operation)
    {
        if (!await ConfirmDoneAsync(operation))
            return false;
        try
        {
            var change = await Session.ApplyAsync(operation);
            if (!change.IsEmpty)
                _status.Show(change.Message);
            ApplyNow();
            return true;
        }
        catch (BoardRuleException e)
        {
            _status.Show(e.Message);
            ApplyNow();
            return false;
        }
        catch (Exception e) when (e is BoardStoreException or BoardConflictException or IOException or UnauthorizedAccessException)
        {
            ApplyNow();
            await _dialogs.ShowDialogAsync(new ConfirmDialogViewModel(L.T("Board"), L.T("The board couldn't be saved."), "OK",
                [e.Message], destructive: false, showCancel: false));
            return false;
        }
    }

    // With the Definition of Done check on Warn: tries the operation without writing and, when it would move
    // issues to Done with unticked criteria or Definition of Done items, asks first (Block refuses in Core).
    private async Task<bool> ConfirmDoneAsync(IBoardOperation operation)
    {
        var snapshot = Snapshot;
        if (snapshot.Project.Settings.DoneCheck != DoneCheck.Warn)
            return true;
        IReadOnlyList<(Issue Issue, IReadOnlyList<string> Missing)> unfinished;
        try
        {
            unfinished = DoneReadiness.Check(snapshot, operation.Apply(snapshot, new OperationContext(Me, DateTimeOffset.Now)));
        }
        catch (BoardRuleException)
        {
            return true;   // refused anyway: RunAsync says why
        }
        if (unfinished.Count == 0)
            return true;
        var text = unfinished.Count == 1
            ? L.T($"{unfinished[0].Issue.Key} \"{unfinished[0].Issue.Summary}\" isn't done by your Definition of Done yet. Move it to Done anyway?")
            : L.T($"{unfinished.Count} issues aren't done by your Definition of Done yet. Move them to Done anyway?");
        var lines = unfinished.Count == 1
            ? unfinished[0].Missing
            : unfinished.Select(u => L.T($"{u.Issue.Key}: {u.Missing.Count} not ticked")).ToList();
        return await _dialogs.ShowDialogAsync(new ConfirmDialogViewModel(L.T("Not Done Yet"), text, L.T("Move to Done"), lines, destructive: false));
    }

    public async Task UndoAsync()
    {
        try
        {
            var what = Session.UndoDescription;
            await Session.UndoAsync();
            _status.Show(L.T("Undone: ") + what);
        }
        catch (BoardRuleException e)
        {
            _status.Show(e.Message);
        }
        catch (Exception e) when (e is BoardStoreException or BoardConflictException or IOException)
        {
            _status.Show(L.T("Undo failed: ") + e.Message);
        }
        ApplyNow();
    }

    public async Task ReloadAsync()
    {
        try
        {
            await Session.ReloadAsync();
        }
        catch (Exception e) when (e is BoardStoreException or IOException or UnauthorizedAccessException)
        {
            _status.Show(L.T("The board couldn't be read again: ") + e.Message);
        }
        ApplyNow();
    }

    // ---- Names for display --------------------------------------------------------------------------------

    public string PersonName(string? id) => id is null ? L.T("Unassigned") : Project.Person(id)?.Name ?? L.T("Someone who left");
    public string StatusName(string? id) => Project.Status(id)?.Name ?? id ?? "";
    public string TypeName(string? id) => Project.IssueType(id)?.Name ?? id ?? "";
    public string PriorityName(string? id) => Project.Priority(id)?.Name ?? L.T("None");

    public string Duration(int? minutes) => minutes is { } m ? WorkDuration.Format(m, Project.Settings) : "";

    internal void Close()
    {
        Session.Changed -= OnSessionChanged;
        Session.Dispose();
    }

    // The session raises on whatever thread wrote or noticed the change; views live on the UI thread.
    private void OnSessionChanged(object? sender, BoardChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            ApplyNow();
        else
            Dispatcher.UIThread.Post(ApplyNow);
    }

    private void ApplyNow()
    {
        if (ReferenceEquals(Snapshot, Session.Snapshot))
            return;
        Snapshot = Session.Snapshot;
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(Title));
        Changed?.Invoke(this, EventArgs.Empty);
        _service.RaiseIssuesChanged();
    }
}
