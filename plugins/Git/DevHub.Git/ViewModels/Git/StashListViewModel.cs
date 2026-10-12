using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Sdk.Core.Formatting;
using DevHub.Git.Core;
using DevHub.Services;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Git;

/// <summary>
/// The Stashes section of a repository tab (G8; Fork's Stashes, JetBrains' Stash / Unstash):
/// the stashes, newest first, and below them the selected one's files and diffs. Stash
/// Changes, Apply (keep the stash), Pop (apply and drop it), Drop (asks).
/// </summary>
public sealed partial class StashListViewModel : ViewModelBase
{
    private readonly GitRepositoryClient? _client;
    private readonly IDialogService _dialogs;
    private readonly IStatusService _status;
    private readonly Func<Task> _refresh;
    private readonly Action _showConflicts;
    private IReadOnlyList<GitStash> _stashes = [];

    /// <param name="showConflicts">Brings up Local Changes (applying stopped on conflicts).</param>
    public StashListViewModel(RepositoryTabLayout layout, GitRepositoryClient? client, IDialogService dialogs,
        IStatusService status, Func<Task> refresh, Action showConflicts,
        Action<FileChangeViewModel, int?>? openInEditor, Action<string, string?>? annotate)
    {
        Layout = layout;
        _client = client;
        _dialogs = dialogs;
        _status = status;
        _refresh = refresh;
        _showConflicts = showConflicts;
        Details = new CommitDetailsViewModel(layout, client, openInEditor, annotate, forStashes: true);
    }

    /// <summary>Details height (shared with All Commits).</summary>
    public RepositoryTabLayout Layout { get; }

    /// <summary>The selected stash's message, files and diffs.</summary>
    public CommitDetailsViewModel Details { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private IReadOnlyList<StashItemViewModel> _items = [];

    public bool HasItems => Items.Count > 0;

    public int Count => _stashes.Count;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(PopCommand), nameof(DropCommand))]
    private StashItemViewModel? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StashChangesCommand), nameof(ApplyCommand), nameof(PopCommand), nameof(DropCommand))]
    private bool _isBusy;

    partial void OnSelectedChanged(StashItemViewModel? value)
    {
        // Same stash after a refresh (new row object): keep the details as they are.
        if (value?.Stash.Id is { } id && Details.Commit?.Id == id)
            return;
        _ = Details.LoadStashAsync(value?.Stash);
    }

    /// <summary>Takes the freshly loaded stashes, keeping the selected one selected (or the newest).</summary>
    public void Update(IReadOnlyList<GitStash> stashes)
    {
        if (stashes.Select(s => s.Id).SequenceEqual(_stashes.Select(s => s.Id)))
            return; // nothing changed (most refreshes are file edits)

        var keep = Selected?.Stash.Id;
        _stashes = stashes;
        var now = DateTimeOffset.Now;
        Items = stashes.Select(s => new StashItemViewModel(s, now)).ToList();
        Selected = Items.FirstOrDefault(i => i.Stash.Id == keep) ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(Count));
    }

    private bool CanRun() => _client is not null && !IsBusy;
    private bool HasSelection() => Selected is not null && CanRun();

    /// <summary>JetBrains' Stash Changes: asks for a message and options, then stashes.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task StashChangesAsync()
    {
        var dialog = new StashDialogViewModel();
        if (!await _dialogs.ShowDialogAsync(dialog))
            return;

        await RunAsync("Stash", async client =>
            _status.Show(await client.StashAsync(dialog.Message, dialog.IncludeUntracked, dialog.KeepIndex)
                ? "Stashed your changes; the files are back at the last commit. Apply or Pop the stash to get them back."
                : dialog.IncludeUntracked ? "No local changes to stash." : "No changes to tracked files to stash (new files aren't included)."));
    }

    /// <summary>Puts the changes back and keeps the stash (to apply it elsewhere too).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ApplyAsync() => ApplyCoreAsync(Selected!.Stash, pop: false);

    /// <summary>Puts the changes back and drops the stash (unless they conflict: then it's kept).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task PopAsync() => ApplyCoreAsync(Selected!.Stash, pop: true);

    private Task ApplyCoreAsync(GitStash stash, bool pop) =>
        RunAsync(pop ? "Pop" : "Apply", async client =>
        {
            if (await client.ApplyStashAsync(stash, pop) == StashApplyResult.Applied)
            {
                _status.Show(pop ? $"Popped \"{stash.Message}\": the changes are back and the stash is gone."
                                 : $"Applied \"{stash.Message}\"; the stash is kept.");
                return;
            }
            _status.Show($"\"{stash.Message}\" clashed with your files: resolve the conflicts in Changes. The stash is kept" +
                         (pop ? "; drop it once you're done." : "."));
            _showConflicts();
        });

    /// <summary>Deletes the stash and its changes, after asking.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DropAsync()
    {
        var stash = Selected!.Stash;
        var confirm = new ConfirmDialogViewModel("Drop Stash",
            $"Delete the stash \"{stash.Message}\"? Its changes are gone unless you applied them somewhere " +
            "(git can only find a dropped stash through hard-to-use recovery commands).", "Drop");
        if (await _dialogs.ShowDialogAsync(confirm))
            await RunAsync("Drop", async client =>
            {
                await client.DropStashAsync(stash);
                _status.Show($"Dropped \"{stash.Message}\".");
            });
    }

    private async Task RunAsync(string what, Func<GitRepositoryClient, Task> operation)
    {
        if (_client is null || IsBusy)
            return;
        IsBusy = true;
        try
        {
            await operation(_client);
        }
        catch (Exception ex)
        {
            // e.g. "Your local changes to the following files would be overwritten": the first line says it.
            _status.Show($"{what} failed: {(ex is GitException git && git.StandardError.Trim() is { Length: > 0 } e ? e.Split('\n')[0].Trim() : ex.Message)}");
        }
        finally
        {
            IsBusy = false;
        }
        await _refresh();
    }
}

/// <summary>A stash row: message, branch it was made on, when, and "+ untracked".</summary>
public sealed class StashItemViewModel(GitStash stash, DateTimeOffset now)
{
    public GitStash Stash { get; } = stash;
    public string Message => Stash.Message;
    public string? Branch => Stash.Branch;
    public string When { get; } = RelativeTime.Format(stash.Date, now);
    public bool HasUntracked => Stash.HasUntracked;
    public string ToolTip => $"{Stash.Ref} · {Stash.Date.ToLocalTime():yyyy-MM-dd HH:mm}" +
                             (Stash.Branch is { } b ? $" · made on {b}" : "") +
                             (Stash.HasUntracked ? " · includes untracked files" : "");

    public override string ToString() => Branch is null ? Message : $"{Message}, on {Branch}";
}
