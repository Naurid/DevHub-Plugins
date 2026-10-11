using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Sdk.Core.Formatting;
using DevHub.Git.Core;
using DevHub.Services;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Git;

/// <summary>
/// Fork's Local Changes view for one repository tab: what changed, split into Unstaged (incl.
/// untracked and conflicts) and Staged, the selected file's diff on the right, and the commit
/// box. Stage / unstage with double-click, Space or the header buttons; discard asks first.
/// </summary>
public partial class LocalChangesViewModel : ViewModelBase
{
    private readonly GitRepositoryClient? _client;
    private readonly IDialogService _dialogs;
    private readonly IStatusService _status;
    private readonly Func<Task> _refresh;

    /// <summary>True while <see cref="Update"/> refills the lists (selection changes then are temporary).</summary>
    private bool _updating;

    /// <param name="layout">Panel widths, shared by all repository tabs.</param>
    /// <param name="client">Git access (null when git isn't installed: everything is disabled).</param>
    /// <param name="dialogs">For the discard confirmation.</param>
    /// <param name="status">Status bar: results and failures.</param>
    /// <param name="refresh">Re-reads the repository status after an operation.</param>
    /// <param name="openInEditor">Opens a file in the external editor for the repository's language, at a line.</param>
    /// <param name="openInTab">Opens a file's diff as its own tab.</param>
    /// <param name="push">Pushes the current branch (Commit and Push); null hides it.</param>
    /// <param name="annotate">Opens a blame tab for a file (path; commit id null = as on disk).</param>
    public LocalChangesViewModel(RepositoryTabLayout layout, GitRepositoryClient? client,
        IDialogService dialogs, IStatusService status, Func<Task> refresh,
        Action<FileChangeViewModel, int?>? openInEditor, Action<FileChangeViewModel>? openInTab,
        Func<Task>? push = null, Action<string, string?>? annotate = null)
    {
        Layout = layout;
        _client = client;
        _dialogs = dialogs;
        _status = status;
        _refresh = refresh;
        Diff = new DiffViewModel(layout, client, openInEditor, openInTab, ApplyPartialAsync, annotate);
        Commit = new CommitViewModel(client, status, () => Staged.Count > 0, refresh, push);

        // The selection lists belong to this ViewModel (the ListBoxes fill them), so these
        // subscriptions live exactly as long as their source and need no unsubscribe.
        UnstagedSelection.CollectionChanged += (_, _) => UnstagedTargetsChanged();
        StagedSelection.CollectionChanged += (_, _) => UnstageSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The commit box (message, Amend, Commit).</summary>
    public CommitViewModel Commit { get; }

    /// <summary>A stage / unstage / discard is running: the buttons wait for it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StageSelectedCommand), nameof(UnstageSelectedCommand),
        nameof(StageAllCommand), nameof(UnstageAllCommand), nameof(DiscardSelectedCommand), nameof(DiscardAllCommand),
        nameof(AcceptYoursCommand), nameof(AcceptTheirsCommand), nameof(MarkResolvedCommand), nameof(OpenMergeToolCommand))]
    private bool _isBusy;

    /// <summary>The changed files of each side (what the commands act on).</summary>
    public ObservableCollection<FileChangeViewModel> Unstaged { get; } = new();
    public ObservableCollection<FileChangeViewModel> Staged { get; } = new();

    /// <summary>
    /// What each list shows: the files (flat), or folders and files in tree order (tree view,
    /// Fork-style; folded folders hide their contents). Rebuilt from <see cref="Unstaged"/> /
    /// <see cref="Staged"/>.
    /// </summary>
    public ObservableCollection<ChangeRowViewModel> UnstagedRows { get; } = new();
    public ObservableCollection<ChangeRowViewModel> StagedRows { get; } = new();

    /// <summary>Folded folders per side (by path), kept across refreshes.</summary>
    private readonly HashSet<string> _collapsedUnstaged = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedStaged = new(StringComparer.Ordinal);

    /// <summary>Tree view (folders) or flat list; shared by all repository tabs and saved.</summary>
    public bool ChangesAsTree
    {
        get => Layout.ChangesAsTree;
        set
        {
            if (Layout.ChangesAsTree == value)
                return;
            Layout.ChangesAsTree = value;
            OnPropertyChanged();
            RebuildRows(staged: false);
            RebuildRows(staged: true);
        }
    }

    public int UnstagedCount => Unstaged.Count;
    public int StagedCount => Staged.Count;

    /// <summary>The diff of the selected file (right-hand pane).</summary>
    public DiffViewModel Diff { get; }

    public bool HasChanges => Unstaged.Count + Staged.Count > 0;

    /// <summary>Panel widths (shared by all repository tabs); the file-list splitter binds to it.</summary>
    public RepositoryTabLayout Layout { get; }

    // One selection across both lists: picking in one clears the other.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StageSelectedCommand), nameof(DiscardSelectedCommand))]
    private FileChangeViewModel? _selectedUnstaged;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnstageSelectedCommand))]
    private FileChangeViewModel? _selectedStaged;

    /// <summary>
    /// The clicked row of each list (a file or a folder; bound to the ListBoxes' SelectedItem).
    /// A file row sets <see cref="SelectedUnstaged"/> / <see cref="SelectedStaged"/>, which the diff shows.
    /// </summary>
    [ObservableProperty] private ChangeRowViewModel? _selectedUnstagedRow;
    [ObservableProperty] private ChangeRowViewModel? _selectedStagedRow;

    /// <summary>
    /// All selected rows of each list (Ctrl+click, Shift+click, Ctrl+A; bound to the ListBoxes'
    /// SelectedItems). Stage / unstage / discard act on these (a folder: every file in it); the
    /// diff shows the clicked file.
    /// </summary>
    public ObservableCollection<ChangeRowViewModel> UnstagedSelection { get; } = new();
    public ObservableCollection<ChangeRowViewModel> StagedSelection { get; } = new();

    /// <summary>What the Unstaged actions act on: the selected rows (folders → their files), or the current row.</summary>
    private IReadOnlyList<FileChangeViewModel> UnstagedTargets => Targets(UnstagedSelection, SelectedUnstagedRow);

    private IReadOnlyList<FileChangeViewModel> StagedTargets => Targets(StagedSelection, SelectedStagedRow);

    private static IReadOnlyList<FileChangeViewModel> Targets(IReadOnlyCollection<ChangeRowViewModel> selection,
        ChangeRowViewModel? current)
    {
        IEnumerable<ChangeRowViewModel> rows = selection.Count > 0 ? selection : current is null ? [] : [current];
        return rows
            .SelectMany(r => r switch
            {
                FileChangeViewModel file => [file],
                ChangeFolderRowViewModel folder => folder.Files,
                _ => [],
            })
            .Distinct()
            .ToList();
    }

    /// <summary>The selected file in either list; the diff pane shows it.</summary>
    public FileChangeViewModel? Selected => SelectedUnstaged ?? SelectedStaged;

    /// <summary>
    /// Replaces the lists with a new status, keeping the selection when the file is still there,
    /// and reloads its diff (the file may have changed even if its status didn't).
    /// </summary>
    public void Update(RepoStatus status)
    {
        _updating = true;
        try
        {
            Refill(status);
        }
        finally
        {
            _updating = false;
        }

        // The rows are new objects, so this reloads the diff; the old lines stay on screen
        // until the new ones arrive (no flicker).
        if (Selected != Diff.File)
            _ = Diff.LoadAsync(Selected);
    }

    private void Refill(RepoStatus status)
    {
        Fill(Unstaged, status.Changes
            .Where(c => c.IsUnstaged || c.IsConflicted)
            // Conflicts first (they block everything), then by path.
            .OrderBy(c => c.IsConflicted ? 0 : 1)
            .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .Select(c => new FileChangeViewModel(c, staged: false, ToggleStage)));
        Fill(Staged, status.Changes
            .Where(c => c.IsStaged)
            .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .Select(c => new FileChangeViewModel(c, staged: true, ToggleStage)));

        // The file objects are new: rebuild the rows and select the same files / folders again.
        RebuildRows(staged: false);
        RebuildRows(staged: true);
        OnPropertyChanged(nameof(ChangesAsTree)); // another tab may have switched the view

        OnPropertyChanged(nameof(UnstagedCount));
        OnPropertyChanged(nameof(StagedCount));
        OnPropertyChanged(nameof(HasChanges));
        StageAllCommand.NotifyCanExecuteChanged();
        UnstageAllCommand.NotifyCanExecuteChanged();
        DiscardAllCommand.NotifyCanExecuteChanged();
        Commit.StagedChanged();
    }

    // ---- Rows: flat list or tree -------------------------------------------------------------

    /// <summary>
    /// Rebuilds one side's rows from its files (flat, or folders + files when <see cref="ChangesAsTree"/>),
    /// keeping the clicked row and the multi-selection (by path) and the folded folders.
    /// </summary>
    private void RebuildRows(bool staged)
    {
        var rows = staged ? StagedRows : UnstagedRows;
        var selection = staged ? StagedSelection : UnstagedSelection;
        var keepCurrent = (staged ? SelectedStagedRow : SelectedUnstagedRow)?.Key;
        var keepSelection = selection.Select(r => r.Key).ToHashSet();

        var wasUpdating = _updating;
        _updating = true; // clearing the list deselects for a moment: don't load an empty diff
        try
        {
            BuildRows(staged ? Staged : Unstaged, rows, staged ? _collapsedStaged : _collapsedUnstaged);
            var current = keepCurrent is null ? null : rows.FirstOrDefault(r => r.Key == keepCurrent);
            if (staged)
                SelectedStagedRow = current;
            else if (current is not null || SelectedStagedRow is null)
                SelectedUnstagedRow = current;
            Reselect(selection, rows, keepSelection);
        }
        finally
        {
            _updating = wasUpdating;
        }
        if (!_updating && Selected != Diff.File)
            _ = Diff.LoadAsync(Selected);
    }

    private void BuildRows(IReadOnlyList<FileChangeViewModel> files, ObservableCollection<ChangeRowViewModel> rows,
        HashSet<string> collapsed) =>
        ChangeRows.Build(files, rows, ChangesAsTree, collapsed, ToggleFolder);

    /// <summary>Folds / unfolds a folder (arrow, double-click, Left / Right) and keeps it selected.</summary>
    private void ToggleFolder(ChangeFolderRowViewModel folder)
    {
        var staged = StagedRows.Contains(folder);
        var collapsed = staged ? _collapsedStaged : _collapsedUnstaged;
        if (!collapsed.Add(folder.Path))
            collapsed.Remove(folder.Path);

        var current = staged ? SelectedStagedRow : SelectedUnstagedRow;
        RebuildRows(staged);
        // The clicked row may have been inside the folded folder: select the folder instead.
        if ((staged ? SelectedStagedRow : SelectedUnstagedRow) is null && current is not null)
        {
            var row = (staged ? StagedRows : UnstagedRows).FirstOrDefault(r => r.Key == folder.Key);
            if (staged)
                SelectedStagedRow = row;
            else
                SelectedUnstagedRow = row;
        }
    }

    /// <summary>Left on a folder row: fold it.</summary>
    [RelayCommand]
    private void CollapseFolder(ChangeRowViewModel? row)
    {
        if (row is ChangeFolderRowViewModel { IsExpanded: true } folder)
            ToggleFolder(folder);
    }

    /// <summary>Right on a folder row: unfold it.</summary>
    [RelayCommand]
    private void ExpandFolder(ChangeRowViewModel? row)
    {
        if (row is ChangeFolderRowViewModel { IsExpanded: false } folder)
            ToggleFolder(folder);
    }

    // ---- Stage / unstage / discard ---------------------------------------------------------

    // ---- Conflicts (G7) ---------------------------------------------------------------------

    /// <summary>Merge / rebase conflict handling; set by the repository tab right after creation.</summary>
    public ConflictOperations? Conflicts { get; set; }

    /// <summary>The selected conflicted files (the conflict menu items act on these).</summary>
    private IReadOnlyList<FileChange> ConflictTargets =>
        UnstagedTargets.Where(f => f.IsConflicted).Select(f => f.Change).ToList();

    private bool HasConflictTargets() => !IsBusy && Conflicts is not null && ConflictTargets.Count > 0;

    [RelayCommand(CanExecute = nameof(HasConflictTargets))]
    private Task AcceptYoursAsync() => Conflicts!.ResolveAsync(ConflictTargets, ConflictChoice.Yours);

    [RelayCommand(CanExecute = nameof(HasConflictTargets))]
    private Task AcceptTheirsAsync() => Conflicts!.ResolveAsync(ConflictTargets, ConflictChoice.Theirs);

    [RelayCommand(CanExecute = nameof(HasConflictTargets))]
    private Task MarkResolvedAsync() => Conflicts!.MarkResolvedAsync(ConflictTargets);

    /// <summary>The merge tool on the current conflicted file (also double-click on a conflict).</summary>
    [RelayCommand(CanExecute = nameof(HasConflictTargets))]
    private Task OpenMergeToolAsync() => Conflicts!.OpenMergeToolAsync(ConflictTargets[0]);

    /// <summary>Double-click on a row: stage it if unstaged, unstage it if staged; a conflict opens the merge tool.</summary>
    private void ToggleStage(FileChangeViewModel file)
    {
        if (file.IsConflicted)
            _ = Conflicts?.OpenMergeToolAsync(file.Change);
        else if (file.IsStagedSide)
            _ = UnstageAsync([file]);
        else
            _ = StageAsync([file]);
    }

    private bool CanStageSelected() => !IsBusy && UnstagedTargets.Count > 0;

    /// <summary>Space in the Unstaged list, or "Stage": every selected row.</summary>
    [RelayCommand(CanExecute = nameof(CanStageSelected))]
    private Task StageSelectedAsync() => StageAsync(UnstagedTargets);

    private bool CanUnstageSelected() => !IsBusy && StagedTargets.Count > 0;

    /// <summary>Space in the Staged list, or "Unstage": every selected row.</summary>
    [RelayCommand(CanExecute = nameof(CanUnstageSelected))]
    private Task UnstageSelectedAsync() => UnstageAsync(StagedTargets);

    private bool CanStageAll() => !IsBusy && _client is not null && Unstaged.Any(f => !f.IsConflicted);

    [RelayCommand(CanExecute = nameof(CanStageAll))]
    private Task StageAllAsync() => StageAsync(Unstaged.ToList());

    private bool CanUnstageAll() => !IsBusy && _client is not null && Staged.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUnstageAll))]
    private Task UnstageAllAsync() => RunAsync("Unstage all", client => client.UnstageAllAsync(), StagedRows, 0);

    private bool CanDiscardSelected() => !IsBusy && UnstagedTargets.Any(f => !f.IsConflicted);

    /// <summary>Throws away the selected files' unstaged changes, after asking (conflicts are left alone).</summary>
    [RelayCommand(CanExecute = nameof(CanDiscardSelected))]
    private Task DiscardSelectedAsync() => DiscardAsync(UnstagedTargets.Where(f => !f.IsConflicted).ToList());

    /// <summary>
    /// Git → Discard Changes… (Ctrl+Alt+Z, window-wide): discards the selected unstaged files,
    /// or says in the status bar why it can't, instead of silently doing nothing.
    /// </summary>
    public Task DiscardSelectionAsync()
    {
        if (IsBusy)
            return Task.CompletedTask;
        var targets = UnstagedTargets;
        if (targets.Count == 0)
        {
            _status.Show(SelectedStaged is not null
                ? "Discard works on unstaged changes. Unstage the file first (Space), then discard it."
                : "Select a file in Unstaged to discard its changes.");
            return Task.CompletedTask;
        }
        if (targets.All(f => f.IsConflicted))
        {
            _status.Show("A conflicted file isn't discarded: resolve it (Accept Yours / Accept Theirs) instead.");
            return Task.CompletedTask;
        }
        return DiscardAsync(targets.Where(f => !f.IsConflicted).ToList());
    }

    private bool CanDiscardAll() => !IsBusy && _client is not null && Unstaged.Any(f => !f.IsConflicted);

    /// <summary>Throws away all unstaged changes (staged ones are kept), after asking.</summary>
    [RelayCommand(CanExecute = nameof(CanDiscardAll))]
    private Task DiscardAllAsync() => DiscardAsync(Unstaged.Where(f => !f.IsConflicted).ToList());

    private async Task StageAsync(IReadOnlyList<FileChangeViewModel> files)
    {
        // Staging a conflicted file means "resolved as it is now": go through Mark as Resolved,
        // which warns when conflict markers are still in it.
        var conflicted = files.Where(f => f.IsConflicted).Select(f => f.Change).ToList();
        if (conflicted.Count > 0 && Conflicts is not null)
            await Conflicts.MarkResolvedAsync(conflicted);

        var rest = files.Where(f => !f.IsConflicted).ToList();
        if (rest.Count == 0)
            return;
        var index = IndexOfSelection(UnstagedRows, SelectedUnstagedRow);
        await RunAsync("Stage", client => client.StageAsync(rest.SelectMany(f => f.Change.StagePaths)), UnstagedRows, index);
    }

    private Task UnstageAsync(IReadOnlyList<FileChangeViewModel> files)
    {
        var index = IndexOfSelection(StagedRows, SelectedStagedRow);
        return RunAsync("Unstage", client => client.UnstageAsync(files.SelectMany(f => f.Change.UnstagePaths)), StagedRows, index);
    }

    private async Task DiscardAsync(IReadOnlyList<FileChangeViewModel> files)
    {
        if (files.Count == 0)
            return;

        // One file: the message already names it. Several: list them, marking the ones deleted.
        var items = files.Count == 1
            ? null
            : files.Select(f => f.IsUntracked ? f.Change.Path + "  (deleted)" : f.Change.Path).ToList();
        var confirm = new ConfirmDialogViewModel("Discard Changes", DiscardMessage(files), "Discard", items);
        if (!await _dialogs.ShowDialogAsync(confirm))
            return;

        var index = IndexOfSelection(UnstagedRows, SelectedUnstagedRow);
        await RunAsync("Discard", client => client.DiscardAsync(files.Select(f => f.Change).ToList()), UnstagedRows, index);
    }

    /// <summary>
    /// The diff pane's hunk / line buttons (G3.1): stage, unstage or discard part of a file.
    /// Discard asks first, naming the file and how many lines go back.
    /// </summary>
    private async Task ApplyPartialAsync(FileDiff diff, Func<int, int, bool> isSelected, PartialAction action)
    {
        if (action == PartialAction.Discard)
        {
            var count = 0;
            for (var h = 0; h < diff.Hunks.Count; h++)
                for (var i = 0; i < diff.Hunks[h].Lines.Count; i++)
                    if (diff.Hunks[h].Lines[i].Kind != DiffLineKind.Context && isSelected(h, i))
                        count++;
            var confirm = new ConfirmDialogViewModel("Discard Selected Changes",
                $"Discard {count} changed line{(count == 1 ? "" : "s")} in {diff.Path}? They go back to their staged or " +
                "last committed version in the file on disk. This can't be undone.", "Discard");
            if (!await _dialogs.ShowDialogAsync(confirm))
                return;
        }

        var (what, list) = action switch
        {
            PartialAction.Stage => ("Stage lines", UnstagedRows),
            PartialAction.Unstage => ("Unstage lines", StagedRows),
            _ => ("Discard lines", UnstagedRows),
        };
        await RunAsync(what, client => client.ApplyPartialAsync(diff, isSelected, action), list, -1);
    }

    /// <summary>
    /// The Git rules: a destructive operation says exactly what will be lost, and only the
    /// sentences that apply (a new file is deleted; a tracked file goes back to an older version).
    /// </summary>
    private static string DiscardMessage(IReadOnlyList<FileChangeViewModel> files)
    {
        const string permanent = "deleted permanently (not moved to the Recycle Bin)";
        if (files.Count == 1)
        {
            var path = files[0].Change.Path;
            return files[0].IsUntracked
                ? $"Delete {path}? It isn't tracked by git, so it is {permanent}."
                : $"Discard your unstaged changes to {path}? It goes back to its staged or last committed version. This can't be undone.";
        }

        var untracked = files.Count(f => f.IsUntracked);
        var message = $"Discard the unstaged changes to {files.Count} files?";
        if (untracked < files.Count)
            message += " Tracked files go back to their staged or last committed version; staged changes are kept.";
        if (untracked > 0)
            message += untracked == 1 ? $" 1 untracked file is {permanent}." : $" {untracked} untracked files are {permanent}.";
        return message + " This can't be undone.";
    }

    /// <summary>
    /// Runs one operation at a time, reports failures in the status bar, refreshes, and then
    /// selects the row that moved into the place of the one acted on (like Fork), so pressing
    /// Space repeatedly stages file after file.
    /// </summary>
    private async Task RunAsync(string what, Func<GitRepositoryClient, Task> operation,
        ObservableCollection<ChangeRowViewModel> rows, int selectIndex)
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
            _status.Show($"{what} failed: {(ex is GitException git && git.StandardError.Trim() is { Length: > 0 } e ? e : ex.Message)}");
        }
        finally
        {
            IsBusy = false;
        }

        await _refresh();
        if (selectIndex >= 0 && rows.Count > 0 && Selected is null
            && (rows == UnstagedRows ? SelectedUnstagedRow : SelectedStagedRow) is null)
        {
            // The file now at that place, else the nearest one before it (in the tree, rows can be folders).
            var at = Math.Min(selectIndex, rows.Count - 1);
            var next = rows.Skip(at).OfType<FileChangeViewModel>().FirstOrDefault()
                       ?? rows.Take(at).OfType<FileChangeViewModel>().LastOrDefault();
            if (rows == UnstagedRows)
                SelectedUnstagedRow = next;
            else
                SelectedStagedRow = next;
        }
    }

    private static int IndexOfSelection(ObservableCollection<ChangeRowViewModel> rows, ChangeRowViewModel? selected) =>
        selected is null ? -1 : rows.IndexOf(selected);

    // The clicked row (file or folder) drives the file selection the diff shows; a folder shows no diff.
    partial void OnSelectedUnstagedRowChanged(ChangeRowViewModel? value)
    {
        if (value is not null)
            SelectedStagedRow = null;
        SelectedUnstaged = value as FileChangeViewModel;
        UnstagedTargetsChanged();
    }

    partial void OnSelectedStagedRowChanged(ChangeRowViewModel? value)
    {
        if (value is not null)
            SelectedUnstagedRow = null;
        SelectedStaged = value as FileChangeViewModel;
        UnstageSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedUnstagedChanged(FileChangeViewModel? value)
    {
        if (value is not null)
        {
            SelectedStaged = null;
            SelectedUnstagedRow = value; // set from code (e.g. after staging): the list follows
        }
        UnstagedTargetsChanged();
        SelectionChanged();
    }

    partial void OnSelectedStagedChanged(FileChangeViewModel? value)
    {
        if (value is not null)
        {
            SelectedUnstaged = null;
            SelectedStagedRow = value;
        }
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(Selected));
        // Picking a file in one list clears the other, which passes through here with the same
        // Selected: only load when it actually differs from what the pane shows. During Update
        // the lists are cleared and refilled; Update loads once at the end.
        if (!_updating && Selected != Diff.File)
            _ = Diff.LoadAsync(Selected);
    }

    private void UnstagedTargetsChanged()
    {
        StageSelectedCommand.NotifyCanExecuteChanged();
        DiscardSelectedCommand.NotifyCanExecuteChanged();
        AcceptYoursCommand.NotifyCanExecuteChanged();
        AcceptTheirsCommand.NotifyCanExecuteChanged();
        MarkResolvedCommand.NotifyCanExecuteChanged();
        OpenMergeToolCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasConflictSelected));
    }

    /// <summary>A conflicted file is selected: the menu shows the conflict items.</summary>
    public bool HasConflictSelected => UnstagedTargets.Any(f => f.IsConflicted);

    private static void Reselect(ObservableCollection<ChangeRowViewModel> selection,
        ObservableCollection<ChangeRowViewModel> rows, HashSet<string> keys)
    {
        if (keys.Count <= 1)
            return; // a single selection is restored through SelectedUnstagedRow / SelectedStagedRow
        foreach (var row in rows.Where(r => keys.Contains(r.Key) && !selection.Contains(r)))
            selection.Add(row);
    }

    private static void Fill(ObservableCollection<FileChangeViewModel> target, IEnumerable<FileChangeViewModel> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}

/// <summary>Kinds of change, for the row color (JetBrains: modified blue, added green, …).</summary>
public enum ChangeColor
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Untracked,
    Conflicted,
}

/// <summary>
/// A row in a file list: a file, or (in tree view) a folder. <see cref="Depth"/> indents it; the
/// lists hold both kinds, so selection, Space and the menus work the same on either.
/// </summary>
public abstract partial class ChangeRowViewModel : ObservableObject
{
    /// <summary>Nesting level in tree view (0 = top; always 0 in the flat list).</summary>
    [ObservableProperty] private int _depth;

    /// <summary>Identifies the row across refreshes (to keep it selected): "F:path" or "D:path".</summary>
    public abstract string Key { get; }
}

/// <summary>A folder in the tree view of Local Changes: fold / unfold; acting on it acts on every file inside.</summary>
public sealed partial class ChangeFolderRowViewModel : ChangeRowViewModel
{
    public ChangeFolderRowViewModel(string name, string path, IReadOnlyList<FileChangeViewModel> files, bool isExpanded,
        Action<ChangeFolderRowViewModel> toggle)
    {
        Name = name;
        Path = path;
        Files = files;
        _isExpanded = isExpanded;
        ToggleCommand = new RelayCommand(() => toggle(this));
    }

    /// <summary>"Views", or "src/DevHub.App" for a folder whose only content is one subfolder.</summary>
    public string Name { get; }

    /// <summary>Full folder path (git form): the key for remembering that it's folded.</summary>
    public string Path { get; }

    /// <summary>Every changed file under this folder, at any depth.</summary>
    public IReadOnlyList<FileChangeViewModel> Files { get; }

    public int Count => Files.Count;

    [ObservableProperty] private bool _isExpanded;

    /// <summary>The arrow, double-click, Left / Right.</summary>
    public RelayCommand ToggleCommand { get; }

    public override string Key => "D:" + Path;

    public string ToolTip => $"{Path} ({Count} changed file{(Count == 1 ? "" : "s")})";

    public override string ToString() => $"Folder {Path}, {Count} files";
}

/// <summary>A file row in Local Changes. In the Staged list it shows the staged status letter, in Unstaged the unstaged one.</summary>
public sealed partial class FileChangeViewModel : ChangeRowViewModel
{
    /// <param name="toggleStage">Double-click: stage (Unstaged list) or unstage (Staged list) this row.</param>
    public FileChangeViewModel(FileChange change, bool staged, Action<FileChangeViewModel>? toggleStage = null)
    {
        Change = change;
        IsStagedSide = staged;
        ToggleStageCommand = new RelayCommand(() => toggleStage?.Invoke(this), () => toggleStage is not null);

        var letter = change.Kind switch
        {
            FileChangeKind.Untracked => '?',
            FileChangeKind.Unmerged => 'U',
            _ => staged ? change.IndexStatus : change.WorkTreeStatus,
        };
        StatusLetter = letter.ToString();
        Color = letter switch
        {
            '?' => ChangeColor.Untracked,
            'U' => ChangeColor.Conflicted,
            'A' => ChangeColor.Added,
            'D' => ChangeColor.Deleted,
            'R' or 'C' => ChangeColor.Renamed,
            _ => ChangeColor.Modified,
        };
        StatusText = Color switch
        {
            ChangeColor.Untracked => "Untracked",
            ChangeColor.Conflicted => "Conflict",
            ChangeColor.Added => "Added",
            ChangeColor.Deleted => "Deleted",
            ChangeColor.Renamed => "Renamed",
            _ => "Modified",
        };
    }

    public FileChange Change { get; }

    /// <summary>Double-click on the row (stage / unstage).</summary>
    public RelayCommand ToggleStageCommand { get; }

    /// <summary>True for the row in the Staged list.</summary>
    public bool IsStagedSide { get; }

    public string FileName => Change.FileName;
    public string Directory => Change.Directory;

    /// <summary>"M", "A", "D", "R", "?", "U".</summary>
    public string StatusLetter { get; }

    public string StatusText { get; }
    public ChangeColor Color { get; }

    // For style classes in the view (one bool per color).
    public bool IsModified => Color == ChangeColor.Modified;
    public bool IsAdded => Color == ChangeColor.Added;
    public bool IsDeleted => Color == ChangeColor.Deleted;
    public bool IsRenamed => Color == ChangeColor.Renamed;
    public bool IsUntracked => Color == ChangeColor.Untracked;
    public bool IsConflicted => Color == ChangeColor.Conflicted;

    /// <summary>Full path, plus where a rename came from.</summary>
    public string ToolTip => Change.OriginalPath is { } from
        ? $"{StatusText}: {from} → {Change.Path}"
        : $"{StatusText}: {Change.Path}";

    public override string Key => "F:" + Change.Path;

    /// <summary>Show the folder next to the name (flat list); in tree view the folder rows say it.</summary>
    [ObservableProperty] private bool _showDirectory = true;

    public override string ToString() => $"{StatusText} {Change.Path}";
}
