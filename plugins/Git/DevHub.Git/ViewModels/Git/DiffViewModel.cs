using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Git.Core;

namespace DevHub.ViewModels.Git;

/// <summary>
/// The diff of one changed file, as rows for the view: unified (<see cref="Rows"/>: hunk headers,
/// then context / removed / added lines with old and new line numbers) and side by side
/// (<see cref="SideRows"/>: old on the left, new on the right). Which one shows is
/// <see cref="RepositoryTabLayout.DiffSideBySide"/>, one switch for every diff. Used by the Local
/// Changes diff pane, commit / stash details and diff tabs.
/// </summary>
/// <remarks>
/// In Local Changes, parts of the file can be staged / unstaged / discarded (G3.1): a hunk with
/// the buttons on its header, or the selected lines (click, Ctrl+click, Shift+click) with the
/// buttons in the pane header or Space. The work is done by the owner (<c>applyPartial</c>),
/// which runs <see cref="GitRepositoryClient.ApplyPartialAsync"/> and refreshes. Side by side,
/// only whole hunks can be picked (lines are selected in the unified view).
/// </remarks>
public partial class DiffViewModel : ViewModelBase
{
    private readonly GitRepositoryClient? _client;
    private readonly Action<FileChangeViewModel, int?>? _openInEditor;
    private readonly Action<FileChangeViewModel>? _openInTab;
    private readonly Func<FileDiff, Func<int, int, bool>, PartialAction, Task>? _applyPartial;
    private readonly Action<string, string?>? _annotate;
    private CancellationTokenSource? _loadCts;

    /// <summary>The diff shown (for building partial patches), or null.</summary>
    private FileDiff? _diff;

    /// <summary>The shown diff is a local change (not a commit), so parts of it can be staged.</summary>
    private bool _isLocalChange;

    /// <summary>The commit the shown file comes from (All Commits, Stashes), or null for a local change.</summary>
    private string? _commitId;

    /// <summary>Where the first change is, so the editor opens there.</summary>
    private int? _firstChangedLine;

    /// <param name="layout">Shared view choices: unified or side by side (the switch in the header).</param>
    /// <param name="client">Git access, or null when git isn't installed.</param>
    /// <param name="openInEditor">Opens the file in the external editor for the repository's language, at a line.</param>
    /// <param name="openInTab">Opens this diff as its own tab; null hides the button (e.g. in that tab).</param>
    /// <param name="applyPartial">Stages / unstages / discards selected lines of the shown diff; null = read-only.</param>
    /// <param name="annotate">Opens a blame tab: file path, commit id (null = the file on disk); null hides Blame.</param>
    public DiffViewModel(RepositoryTabLayout layout, GitRepositoryClient? client,
        Action<FileChangeViewModel, int?>? openInEditor = null,
        Action<FileChangeViewModel>? openInTab = null,
        Func<FileDiff, Func<int, int, bool>, PartialAction, Task>? applyPartial = null,
        Action<string, string?>? annotate = null)
    {
        Layout = layout;
        _client = client;
        _openInEditor = openInEditor;
        _openInTab = openInTab;
        _applyPartial = applyPartial;
        _annotate = annotate;

        // Owned by this ViewModel (the ListBox fills it): no unsubscribe needed.
        SelectedRows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedChangeCount));
            OnPropertyChanged(nameof(HasSelectedChanges));
            StageLinesCommand.NotifyCanExecuteChanged();
            UnstageLinesCommand.NotifyCanExecuteChanged();
            DiscardLinesCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Shared view choices; the view binds <see cref="RepositoryTabLayout.DiffSideBySide"/> directly,
    /// so flipping the switch in one diff flips every open diff.</summary>
    public RepositoryTabLayout Layout { get; }

    /// <summary>The file shown, or null (nothing selected).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile), nameof(PlaceholderText))]
    [NotifyCanExecuteChangedFor(nameof(OpenInEditorCommand), nameof(OpenInTabCommand), nameof(AnnotateCommand))]
    private FileChangeViewModel? _file;

    public bool HasFile => File is not null;

    /// <summary>"Staged" / "Unstaged" (+ "renamed from …"), under the file name.</summary>
    [ObservableProperty] private string? _subtitle;

    /// <summary>"+12" and "−3" in the header; null when there are no line changes.</summary>
    [ObservableProperty] private string? _addedText;
    [ObservableProperty] private string? _removedText;

    /// <summary>The lines. Replaced as a whole on each load (one change notification, not thousands).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows), nameof(BannerText), nameof(PlaceholderText))]
    private IReadOnlyList<DiffRowViewModel> _rows = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>The same lines side by side (built with <see cref="Rows"/>, so switching views is instant).</summary>
    [ObservableProperty] private IReadOnlyList<SideBySideRowViewModel> _sideRows = [];

    /// <summary>Binary file, folder, no changes, an error, or "cut off" (the only one that comes with lines).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BannerText), nameof(PlaceholderText))]
    private string? _message;

    /// <summary>The message as a strip above the lines (when there are lines).</summary>
    public string? BannerText => HasRows ? Message : null;

    /// <summary>
    /// What the pane says when there are no lines: the message, or a hint when nothing is
    /// selected. Empty while the first diff loads (a "Loading…" flash would only flicker).
    /// </summary>
    public string? PlaceholderText => HasRows ? null : Message ?? (HasFile ? null : "Select a file to see its changes.");

    public bool CanOpenInEditor => _openInEditor is not null;
    public bool CanOpenInTab => _openInTab is not null;
    public bool CanAnnotate => _annotate is not null;

    // ---- Partial staging (G3.1) ----------------------------------------------------------------

    /// <summary>The selected rows (the ListBox's SelectedItems). Only added / removed lines count.</summary>
    public ObservableCollection<DiffRowViewModel> SelectedRows { get; } = new();

    public int SelectedChangeCount => SelectedRows.Count(r => r.IsChange);
    public bool HasSelectedChanges => SelectedChangeCount > 0;

    /// <summary>Lines can be picked out of this diff: a local text change that a patch can express.</summary>
    private bool SupportsParts =>
        _applyPartial is not null && _isLocalChange && File is { IsConflicted: false }
        && _diff is { IsBinary: false, IsCombined: false, IsRenamed: false, IsTruncated: false, IsDirectory: false, Hunks.Count: > 0 };

    /// <summary>Unstaged diff: hunks and lines can be staged or discarded.</summary>
    public bool CanStageParts => SupportsParts && File is { IsStagedSide: false };

    /// <summary>Staged diff: hunks and lines can be unstaged.</summary>
    public bool CanUnstageParts => SupportsParts && File is { IsStagedSide: true };

    [RelayCommand]
    private Task StageHunkAsync(DiffRowViewModel? hunk) => ApplyHunkAsync(hunk, PartialAction.Stage);

    [RelayCommand]
    private Task UnstageHunkAsync(DiffRowViewModel? hunk) => ApplyHunkAsync(hunk, PartialAction.Unstage);

    [RelayCommand]
    private Task DiscardHunkAsync(DiffRowViewModel? hunk) => ApplyHunkAsync(hunk, PartialAction.Discard);

    [RelayCommand(CanExecute = nameof(CanStageLines))]
    private Task StageLinesAsync() => ApplyLinesAsync(PartialAction.Stage);

    [RelayCommand(CanExecute = nameof(CanUnstageLines))]
    private Task UnstageLinesAsync() => ApplyLinesAsync(PartialAction.Unstage);

    [RelayCommand(CanExecute = nameof(CanStageLines))]
    private Task DiscardLinesAsync() => ApplyLinesAsync(PartialAction.Discard);

    private bool CanStageLines() => CanStageParts && HasSelectedChanges;
    private bool CanUnstageLines() => CanUnstageParts && HasSelectedChanges;

    /// <summary>Space in the diff: stage (Unstaged) or unstage (Staged) the selected lines.</summary>
    [RelayCommand]
    private Task ToggleLinesAsync() =>
        CanStageLines() ? ApplyLinesAsync(PartialAction.Stage)
        : CanUnstageLines() ? ApplyLinesAsync(PartialAction.Unstage)
        : Task.CompletedTask;

    private Task ApplyHunkAsync(DiffRowViewModel? hunk, PartialAction action) =>
        hunk is { IsHunk: true } && _diff is { } diff && SupportsParts
            ? _applyPartial!(diff, (h, _) => h == hunk.HunkIndex, action)
            : Task.CompletedTask;

    private Task ApplyLinesAsync(PartialAction action)
    {
        if (_diff is not { } diff || !SupportsParts)
            return Task.CompletedTask;
        var chosen = SelectedRows.Where(r => r.IsChange).Select(r => (r.HunkIndex, r.LineIndex)).ToHashSet();
        return chosen.Count == 0 ? Task.CompletedTask : _applyPartial!(diff, (h, i) => chosen.Contains((h, i)), action);
    }

    private void PartsChanged()
    {
        OnPropertyChanged(nameof(CanStageParts));
        OnPropertyChanged(nameof(CanUnstageParts));
        StageLinesCommand.NotifyCanExecuteChanged();
        UnstageLinesCommand.NotifyCanExecuteChanged();
        DiscardLinesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Shows <paramref name="file"/>'s diff (null clears the pane). Called whenever the selection
    /// changes and after every status refresh (the list items are new objects then), so the
    /// diff follows edits made outside DevHub.
    /// </summary>
    public Task LoadAsync(FileChangeViewModel? file)
    {
        var client = _client;
        return LoadCoreAsync(file,
            client is null ? null : ct => client.GetDiffAsync(file!.Change, file.IsStagedSide, ct),
            file is null ? "" : file.IsConflicted ? "Conflict" : file.IsStagedSide ? "Staged" : "Unstaged",
            isLocalChange: true);
    }

    /// <summary>Shows a file as a commit changed it (All Commits), compared with the commit's first parent.</summary>
    public Task LoadCommitFileAsync(GitCommit commit, FileChangeViewModel? file)
    {
        var client = _client;
        return LoadCoreAsync(file,
            client is null ? null : ct => client.GetCommitDiffAsync(commit, file!.Change, ct),
            $"Commit {commit.ShortId}", isLocalChange: false, commitId: commit.Id);
    }

    private async Task LoadCoreAsync(FileChangeViewModel? file, Func<CancellationToken, Task<FileDiff>>? load, string side,
        bool isLocalChange, string? commitId = null)
    {
        _loadCts?.Cancel();
        _isLocalChange = isLocalChange;
        _commitId = commitId;
        File = file;

        if (file is null || load is null)
        {
            Show(null, null, "");
            return;
        }

        var cts = _loadCts = new CancellationTokenSource();
        try
        {
            var diff = await load(cts.Token);
            if (!cts.IsCancellationRequested)
                Show(file, diff, side);
        }
        catch (OperationCanceledException)
        {
            // A newer load replaced this one.
        }
        catch (Exception ex)
        {
            // Fire-and-forget caller: show the problem in the pane instead of losing it.
            Rows = [];
            SideRows = [];
            Message = "Couldn't load the diff: " + ex.Message;
        }
    }

    /// <summary>Clears the pane and shows a message instead (e.g. "no changes any more").</summary>
    public void ShowMessage(string message)
    {
        _loadCts?.Cancel();
        File = null;
        Show(null, null, "");
        Message = message;
    }

    /// <summary>"Open in Editor" (F4, JetBrains' Jump to Source): the real file in the external editor.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private void OpenInEditor()
    {
        if (File is not null)
            _openInEditor?.Invoke(File, _firstChangedLine);
    }

    // A deleted file or an untracked folder has nothing to open.
    private bool CanOpenFile() => File is { IsDeleted: false } file && !file.Change.Path.EndsWith('/') && _openInEditor is not null;

    /// <summary>"Open in New Tab": this diff as its own tab, for more room.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenTab))]
    private void OpenInTab()
    {
        if (File is not null)
            _openInTab?.Invoke(File);
    }

    private bool CanOpenTab() => File is not null && _openInTab is not null;

    /// <summary>
    /// JetBrains' "Annotate with Git Blame": who last changed each line, in a tab. For a local
    /// change the file as it is on disk; in a commit (or stash), the file as it was there.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAnnotateFile))]
    private void Annotate()
    {
        if (File is not null)
            _annotate?.Invoke(File.Change.Path, _commitId);
    }

    // Nothing to blame in a deleted file, or in one git has no history for yet (untracked / newly added, on disk).
    private bool CanAnnotateFile() =>
        _annotate is not null && File is { IsDeleted: false, IsUntracked: false } file && !file.Change.Path.EndsWith('/')
        && !(_commitId is null && file is { IsAdded: true });

    /// <param name="side">Where the diff comes from, for the subtitle: "Staged", "Unstaged", "Commit 1a2b3c4d"…</param>
    private void Show(FileChangeViewModel? file, FileDiff? diff, string side)
    {
        _firstChangedLine = diff?.FirstChangedLine;
        _diff = diff;
        SelectedRows.Clear(); // the rows are new objects
        PartsChanged();
        if (file is null || diff is null)
        {
            Subtitle = AddedText = RemovedText = Message = null;
            Rows = [];
            SideRows = [];
            return;
        }

        Subtitle = diff.IsRenamed ? $"{side} · renamed from {diff.OldPath}" : side;
        AddedText = diff.AddedCount > 0 ? $"+{diff.AddedCount}" : null;
        RemovedText = diff.RemovedCount > 0 ? $"−{diff.RemovedCount}" : null;
        var rows = BuildRows(diff);
        SideRows = BuildSideRows(diff, rows);
        Rows = rows;
        Message = diff switch
        {
            { IsDirectory: true } => "New folder, not tracked yet: git lists it as one entry. Its files show up one by one once the folder is staged.",
            { IsBinary: true } => "Binary file: no text to compare.",
            { ModeChange: { } mode, Hunks.Count: 0 } => $"Only the file mode changed ({mode}).",
            { IsRenamed: true, Hunks.Count: 0 } => "Renamed without changes to the content.",
            { IsTruncated: true } => $"Long diff: showing the first {DiffParser.DefaultMaxLines:N0} lines.",
            { Hunks.Count: 0 } => "No changes to show.",
            _ => null,
        };
    }

    private static List<DiffRowViewModel> BuildRows(FileDiff diff)
    {
        var rows = new List<DiffRowViewModel>();
        for (var h = 0; h < diff.Hunks.Count; h++)
        {
            var hunk = diff.Hunks[h];
            rows.Add(DiffRowViewModel.ForHunk(hunk, h));
            for (var i = 0; i < hunk.Lines.Count; i++)
            {
                var line = hunk.Lines[i];
                rows.Add(DiffRowViewModel.ForLine(line, h, i));
                if (line.NoNewlineAtEnd)
                    rows.Add(DiffRowViewModel.ForNote("No newline at end of file"));
            }
        }
        return rows;
    }

    /// <param name="rows">The unified rows: a side-by-side hunk header hands its unified header to the hunk commands.</param>
    private static List<SideBySideRowViewModel> BuildSideRows(FileDiff diff, List<DiffRowViewModel> rows)
    {
        var hunkRows = rows.Where(r => r.IsHunk).ToList();
        return SideBySideLayout.Build(diff)
            .Select(r => r.IsHunkHeader ? SideBySideRowViewModel.ForHunk(r.Hunk!, hunkRows[r.HunkIndex]) : SideBySideRowViewModel.ForLines(r))
            .ToList();
    }
}

/// <summary>One row of the side-by-side diff: a hunk header across both halves, or an old and a new half.</summary>
public sealed class SideBySideRowViewModel
{
    private SideBySideRowViewModel(DiffSideViewModel left, DiffSideViewModel right)
    {
        Left = left;
        Right = right;
    }

    /// <summary>The old file's half (removed or context line, or a filler).</summary>
    public DiffSideViewModel Left { get; }

    /// <summary>The new file's half (added or context line, or a filler).</summary>
    public DiffSideViewModel Right { get; }

    public bool IsHunk => HunkRow is not null;

    /// <summary>The hunk header text, across both halves.</summary>
    public string Header { get; private init; } = "";

    /// <summary>The unified row of the same hunk header (the hunk commands' parameter), or null for a line row.</summary>
    public DiffRowViewModel? HunkRow { get; private init; }

    public static SideBySideRowViewModel ForHunk(DiffHunk hunk, DiffRowViewModel hunkRow) =>
        new(DiffSideViewModel.Filler, DiffSideViewModel.Filler) { Header = hunk.Header, HunkRow = hunkRow };

    public static SideBySideRowViewModel ForLines(SideBySideRow row) =>
        new(DiffSideViewModel.For(row.Left, old: true), DiffSideViewModel.For(row.Right, old: false));

    public override string ToString() => IsHunk ? Header : $"{Left.Text} | {Right.Text}";
}

/// <summary>One half of a side-by-side row: a line number and text, or a filler (no line on this side).</summary>
public sealed class DiffSideViewModel
{
    public static readonly DiffSideViewModel Filler = new() { IsFiller = true };

    public string Number { get; private init; } = "";

    /// <summary>"+", "-" or "" (its own column, as in the unified view).</summary>
    public string Sign => IsAdded ? "+" : IsRemoved ? "-" : "";

    public string Text { get; private init; } = "";
    public bool IsAdded { get; private init; }
    public bool IsRemoved { get; private init; }
    public bool IsFiller { get; private init; }

    /// <param name="old">The left half: show the old line number.</param>
    public static DiffSideViewModel For(DiffLine? line, bool old) =>
        line is null
            ? Filler
            : new DiffSideViewModel
            {
                Number = (old ? line.OldLineNumber : line.NewLineNumber)?.ToString() ?? "",
                // TextBlock doesn't do tab stops; 4 spaces keep indentation readable (as in the unified view).
                Text = line.Text.Replace("\t", "    ", StringComparison.Ordinal),
                IsAdded = line.Kind == DiffLineKind.Added,
                IsRemoved = line.Kind == DiffLineKind.Removed,
            };
}

/// <summary>One row of the diff view: a hunk header, a line, or a note.</summary>
public sealed class DiffRowViewModel
{
    private DiffRowViewModel(string oldNumber, string newNumber, string sign, string text)
    {
        OldNumber = oldNumber;
        NewNumber = newNumber;
        Sign = sign;
        Text = text;
    }

    public string OldNumber { get; }
    public string NewNumber { get; }

    /// <summary>"+", "-" or "" (shown in its own column, like JetBrains' unified view).</summary>
    public string Sign { get; }

    public string Text { get; }

    public bool IsHunk { get; private init; }
    public bool IsAdded { get; private init; }
    public bool IsRemoved { get; private init; }
    public bool IsNote { get; private init; }

    /// <summary>Shown in the secondary color (hunk headers, notes).</summary>
    public bool IsMuted => IsHunk || IsNote;

    /// <summary>An added or removed line: the only rows partial staging acts on.</summary>
    public bool IsChange => IsAdded || IsRemoved;

    /// <summary>Which hunk of the file this row belongs to (-1 for notes).</summary>
    public int HunkIndex { get; private init; } = -1;

    /// <summary>The line's index inside its hunk (-1 for hunk headers and notes).</summary>
    public int LineIndex { get; private init; } = -1;

    public static DiffRowViewModel ForHunk(DiffHunk hunk, int hunkIndex) =>
        new("", "", "", hunk.Header) { IsHunk = true, HunkIndex = hunkIndex };

    public static DiffRowViewModel ForNote(string text) =>
        new("", "", "", text) { IsNote = true };

    public static DiffRowViewModel ForLine(DiffLine line, int hunkIndex, int lineIndex) =>
        new(line.OldLineNumber?.ToString() ?? "", line.NewLineNumber?.ToString() ?? "",
            line.Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "-", _ => "" },
            // TextBlock doesn't do tab stops; 4 spaces keep indentation readable.
            line.Text.Replace("\t", "    ", StringComparison.Ordinal))
        {
            IsAdded = line.Kind == DiffLineKind.Added,
            IsRemoved = line.Kind == DiffLineKind.Removed,
            HunkIndex = hunkIndex,
            LineIndex = lineIndex,
        };

    public override string ToString() => $"{Sign}{Text}";
}
