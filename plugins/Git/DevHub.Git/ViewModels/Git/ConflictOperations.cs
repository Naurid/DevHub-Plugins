using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Git.Core;
using DevHub.Services;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Git;

/// <summary>
/// A merge / rebase / cherry-pick / revert in progress, for one repository (G7): the banner
/// across the tab ("Merge in progress: 2 conflicts"), Continue / Skip / Abort, and resolving
/// conflicted files (Accept Yours / Accept Theirs, Mark as Resolved, the merge tool). The
/// Local Changes list shows the conflicted files first, in red.
/// </summary>
public sealed partial class ConflictOperations : ObservableObject
{
    private readonly GitRepositoryClient? _client;
    private readonly IDialogService _dialogs;
    private readonly IStatusService _status;
    private readonly Func<Task> _refresh;
    private readonly Action<string> _openInEditor;

    /// <param name="openInEditor">Opens a file (repository-relative path) in the external editor.</param>
    public ConflictOperations(GitRepositoryClient? client, IDialogService dialogs, IStatusService status,
        Func<Task> refresh, Action<string> openInEditor)
    {
        _client = client;
        _dialogs = dialogs;
        _status = status;
        _refresh = refresh;
        _openInEditor = openInEditor;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(Title), nameof(Hint), nameof(CanSkip), nameof(YoursHint), nameof(TheirsHint))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand), nameof(AbortCommand), nameof(SkipCommand))]
    private RepositoryOperation _operation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private int _conflictCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand), nameof(AbortCommand), nameof(SkipCommand))]
    private bool _isBusy;

    /// <summary>Something is in progress: the banner shows.</summary>
    public bool IsActive => Operation != RepositoryOperation.None;

    public string Title => Operation switch
    {
        RepositoryOperation.Merging => "Merge in progress",
        RepositoryOperation.Rebasing => "Rebase in progress",
        RepositoryOperation.CherryPicking => "Cherry-pick in progress",
        RepositoryOperation.Reverting => "Revert in progress",
        _ => "",
    };

    /// <summary>What to do next, under the title.</summary>
    public string Hint => ConflictCount switch
    {
        0 => Operation == RepositoryOperation.Merging
            ? "All conflicts are resolved: Continue to commit the merge."
            : "All conflicts are resolved: Continue.",
        1 => "1 conflicted file: resolve it in Changes (right-click: Accept Yours / Theirs, Merge Tool), then Continue.",
        var n => $"{n} conflicted files: resolve them in Changes (right-click: Accept Yours / Theirs, Merge Tool), then Continue.",
    };

    /// <summary>Only a rebase can skip the commit that stopped it.</summary>
    public bool CanSkip => Operation == RepositoryOperation.Rebasing;

    /// <summary>What "yours" / "theirs" mean right now (menu tooltips).</summary>
    public string YoursHint => Operation == RepositoryOperation.Rebasing
        ? "Keep your commit's version (the commit being replayed)"
        : "Keep the current branch's version";

    public string TheirsHint => Operation == RepositoryOperation.Rebasing
        ? "Keep the version of the branch you're rebasing onto"
        : "Keep the incoming (merged) branch's version";

    /// <summary>Called after every repository refresh.</summary>
    public void Update(RepositoryOperation operation, RepoStatus status)
    {
        Operation = operation;
        ConflictCount = status.Changes.Count(c => c.IsConflicted);
    }

    private bool CanContinue() => IsActive && ConflictCount == 0 && !IsBusy && _client is not null;
    private bool CanAbort() => IsActive && !IsBusy && _client is not null;
    private bool CanSkipNow() => CanSkip && !IsBusy && _client is not null;

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private Task ContinueAsync() => RunAsync("Continue", async client => Report(await client.ContinueAsync()));

    /// <summary>Rebase: drop the commit that stopped on conflicts. Its changes are lost from the branch, so ask.</summary>
    [RelayCommand(CanExecute = nameof(CanSkipNow))]
    private async Task SkipAsync()
    {
        var confirm = new ConfirmDialogViewModel("Skip Commit",
            "Leave out the commit that stopped the rebase? Its changes won't be in the rebased branch " +
            "(the original commit stays reachable through the reflog for a while).", "Skip Commit");
        if (await _dialogs.ShowDialogAsync(confirm))
            await RunAsync("Skip", async client => Report(await client.SkipAsync()));
    }

    /// <summary>Cancel the whole operation: everything goes back to how it was, resolutions included. Asks.</summary>
    [RelayCommand(CanExecute = nameof(CanAbort))]
    private async Task AbortAsync()
    {
        var what = Title.Replace(" in progress", "").ToLowerInvariant();
        var confirm = new ConfirmDialogViewModel($"Abort {Title.Split(' ')[0]}",
            $"Abort the {what}? The branch and files go back to how they were before it started; " +
            "conflicts you've resolved so far are thrown away.", "Abort");
        if (await _dialogs.ShowDialogAsync(confirm))
            await RunAsync("Abort", async client =>
            {
                await client.AbortAsync();
                _status.Show($"Aborted the {what}; everything is back as before.");
            });
    }

    /// <summary>
    /// Accept Yours / Accept Theirs for the given conflicted files: the file becomes that version
    /// and is marked resolved. Asks first (the other side's changes, and any hand edits, go).
    /// </summary>
    public async Task ResolveAsync(IReadOnlyList<FileChange> files, ConflictChoice choice)
    {
        files = files.Where(f => f.IsConflicted).ToList();
        if (files.Count == 0)
            return;

        var side = choice == ConflictChoice.Yours ? "your" : "the incoming";
        var confirm = new ConfirmDialogViewModel(choice == ConflictChoice.Yours ? "Accept Yours" : "Accept Theirs",
            files.Count == 1
                ? $"Keep {side} version of {files[0].Path}? The other side's changes to it (and any edits you made to resolve it) are dropped."
                : $"Keep {side} version of {files.Count} files? The other side's changes to them (and any edits you made) are dropped.",
            choice == ConflictChoice.Yours ? "Accept Yours" : "Accept Theirs",
            files.Count == 1 ? null : files.Select(f => f.Path).ToList(), destructive: false);
        if (!await _dialogs.ShowDialogAsync(confirm))
            return;

        await RunAsync("Resolve", async client =>
        {
            foreach (var file in files)
                await client.ResolveAsync(file, choice);
            _status.Show(files.Count == 1 ? $"Resolved {files[0].Path}." : $"Resolved {files.Count} files.");
        });
    }

    /// <summary>The files were fixed by hand: mark them resolved, warning if conflict markers are left.</summary>
    public async Task MarkResolvedAsync(IReadOnlyList<FileChange> files)
    {
        if (_client is null || (files = files.Where(f => f.IsConflicted).ToList()).Count == 0)
            return;

        var withMarkers = new List<string>();
        foreach (var file in files)
            if (await _client.HasConflictMarkersAsync(file.Path))
                withMarkers.Add(file.Path);
        if (withMarkers.Count > 0)
        {
            var confirm = new ConfirmDialogViewModel("Conflict Markers Left",
                "These files still contain conflict markers (<<<<<<< … >>>>>>>). Mark them as resolved anyway? " +
                "The markers would end up in the commit.", "Mark as Resolved", withMarkers);
            if (!await _dialogs.ShowDialogAsync(confirm))
                return;
        }

        await RunAsync("Mark as resolved", async client =>
        {
            await client.MarkResolvedAsync(files.Select(f => f.Path));
            _status.Show(files.Count == 1 ? $"Marked {files[0].Path} as resolved." : $"Marked {files.Count} files as resolved.");
        });
    }

    /// <summary>
    /// Opens the configured merge tool (<c>merge.tool</c>) on the file and waits for it (status
    /// bar, with Cancel). Without one, offers to open the file in the editor to fix the markers by hand.
    /// </summary>
    public async Task OpenMergeToolAsync(FileChange file)
    {
        if (_client is null || IsBusy)
            return;

        if (await _client.GetMergeToolAsync() is not { } tool)
        {
            var open = new ConfirmDialogViewModel("No Merge Tool",
                "No merge tool is configured in git (merge.tool). You can open the file in your editor and resolve the " +
                "<<<<<<< … >>>>>>> sections by hand, then Mark as Resolved. (To set up a tool: " +
                "\"git config --global merge.tool <name>\", e.g. vscode, meld, kdiff3, p4merge.)",
                "Open in Editor", destructive: false);
            if (await _dialogs.ShowDialogAsync(open))
                _openInEditor(file.Path);
            return;
        }

        using var cts = new CancellationTokenSource();
        IsBusy = true;
        var task = _status.StartTask($"Waiting for {tool}: {file.FileName}…", cts.Cancel);
        try
        {
            await _client.RunMergeToolAsync(file.Path, cts.Token);
            _status.Show($"{tool} finished: {file.Path} is resolved.");
        }
        catch (OperationCanceledException)
        {
            _status.Show("Merge tool closed; the file is still conflicted.");
        }
        catch (Exception ex)
        {
            _status.Show($"{tool}: {Describe(ex)}");
        }
        finally
        {
            task.Dispose();
            IsBusy = false;
            await _refresh();
        }
    }

    private void Report(OperationOutcome outcome) =>
        _status.Show(outcome switch
        {
            OperationOutcome.Conflicts => "Stopped on conflicts again (the next commit): resolve them, then Continue.",
            _ => $"{Title.Replace(" in progress", "")} finished.",
        });

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
            _status.Show($"{what} failed: {Describe(ex)}");
        }
        finally
        {
            IsBusy = false;
        }
        await _refresh();
    }

    // "error: …\nhint: …": the first line says it; the rest is in the Git Console.
    private static string Describe(Exception ex) =>
        ex is GitException git && git.StandardError.Trim() is { Length: > 0 } e ? e.Split('\n')[0].Trim() : ex.Message;
}
