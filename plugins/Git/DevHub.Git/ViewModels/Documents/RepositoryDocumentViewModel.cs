using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Controls;
using DevHub.Git.Core;
using DevHub.Services;
using DevHub.ViewModels.Git;

namespace DevHub.ViewModels.Documents;

/// <summary>
/// One repository open as an editor tab: Fork's per-repository workspace (a sidebar with
/// Local Changes, All Commits, Branches, Remotes, Tags, Stashes, Submodules) in JetBrains style. Several can
/// be open side by side. Created by <see cref="RepositoryDocumentFactory"/>; disposed when its
/// tab closes.
/// </summary>
/// <remarks>
/// G1: status (branch, ahead/behind, changed files, merge/rebase in progress), refreshed when the
/// tab opens, when it becomes active, and whenever the repository changes on disk (a
/// <see cref="RepositoryWatcher"/>, so background tabs stay current too). Everything here only
/// reads the repository.
/// </remarks>
public sealed partial class RepositoryDocumentViewModel : DocumentViewModelBase, IDisposable
{
    public const string IdPrefix = "Repository:";

    private readonly GitRepositoryClient? _client;
    private readonly RepositoryWatcher? _watcher;
    private readonly BranchOperations _branchOperations;
    private readonly CommitOperations _commitOperations;
    private readonly TerminalLauncher _terminal;
    private CancellationTokenSource? _refreshCts;
    private Task _refreshTask = Task.CompletedTask;
    private bool _disposed;

    /// <param name="path">Repository root folder.</param>
    /// <param name="client">Git access for this repository, or null when git isn't installed.</param>
    /// <param name="layout">Panel widths, shared with the other repository tabs.</param>
    /// <param name="services">External editor, dialogs, status bar.</param>
    public RepositoryDocumentViewModel(string path, GitRepositoryClient? client, RepositoryTabLayout layout,
        RepositoryTabServices services)
    {
        Path = path;
        _client = client;
        _terminal = services.Terminal;
        Layout = layout;
        Action<FileChangeViewModel, int?> openInEditor = (file, line) => services.Editor.Open(path, file.Change.Path, line);
        LocalChanges = new LocalChangesViewModel(layout, client, services.Dialogs, services.Status, RefreshAsync,
            openInEditor, OpenDiffInTab,
            push: () => Remote!.PushWithoutAskingAsync(), // Remote is created below; this runs later
            annotate: OpenBlame);
        Name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;
        _selectedSection = LocalChangesSection;
        _selectedNode = LocalChangesSection;

        var branchOperations = new BranchOperations(client, services.Dialogs, services.Status, RefreshAsync);
        _branchOperations = branchOperations;
        _branchTree = new SidebarBranchTree(branchOperations);
        BranchPopup = new BranchPopupViewModel(branchOperations);
        Remote = new RemoteOperations(client, services.Dialogs, services.Status, RefreshAsync,
            () => branchOperations.Branches, Name, services.Host);

        // Fork's sidebar: sections, then Branches and Remotes as folding trees, then more sections.
        BranchesGroup = new SidebarGroupNode("Local", IconKind.Vcs)
        {
            NewBranchCommand = new AsyncRelayCommand(() => branchOperations.NewBranchAsync()),
        };
        RemotesGroup = new SidebarGroupNode("Remotes", IconKind.Fetch)
        {
            ManageRemotesCommand = Remote.ManageRemotesCommand,
            PublishCommand = Remote.PublishCommand,
            PublishText = Remote.PublishText,
            FetchCommand = Remote.FetchCommand,
        };
        Sidebar = [LocalChangesSection, AllCommitsSection, BranchesGroup, RemotesGroup, TagsSection, StashesSection, SubmodulesSection];

        _commitOperations = new CommitOperations(client, services.Dialogs, services.Status, RefreshAsync, branchOperations, Remote);
        CommitLog = new CommitLogViewModel(layout, client, openInEditor, OpenBlame, _commitOperations, services.Host);
        Tags = new TagListViewModel(_commitOperations, branchOperations);
        Stashes = new StashListViewModel(layout, client, services.Dialogs, services.Status, RefreshAsync,
            () => SelectedSection = LocalChangesSection, openInEditor, OpenBlame);
        Submodules = new SubmoduleListViewModel(client, Remote, services.Status,
            folder => OpenDocument?.Invoke(RepositoryDocumentFactory.IdFor(folder)));
        Conflicts = new ConflictOperations(client, services.Dialogs, services.Status, RefreshAsync,
            file => services.Editor.Open(path, file, null));
        LocalChanges.Conflicts = Conflicts;
        // A merge / rebase that stops on conflicts brings Local Changes up, where they're listed.
        branchOperations.ShowConflicts = () => SelectedSection = LocalChangesSection;

        if (_client is null)
            return;

        try
        {
            _watcher = new RepositoryWatcher(path);
            _watcher.Changed += OnRepositoryChanged;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Folder gone or unreadable: the refresh below reports it on the tab.
        }

        // One git status per open tab is cheap, and it gives every tab its dirty dot at startup.
        _refreshTask = RefreshAsync();
    }

    public override string Id => IdPrefix + Path;
    public override string Title => Name;
    public override string Icon => IconKind.Vcs;

    /// <summary>The sidebar runs edge to edge (Fork-style), so no editor-area padding.</summary>
    public override bool FillsEditorArea => true;

    /// <summary>Repository root folder.</summary>
    public string Path { get; }

    public override string? ContextPath => Path;

    /// <summary>Folder name, shown as the tab title.</summary>
    public string Name { get; }

    /// <summary>False when git isn't installed; the view then shows how to fix that.</summary>
    public bool IsGitAvailable => _client is not null;

    /// <summary>Checked-out branch, or the short commit id when HEAD is detached; null until loaded.</summary>
    [ObservableProperty] private string? _branch;

    /// <summary>"↑2 ↓1" against the upstream, or null when in sync / no upstream.</summary>
    [ObservableProperty] private string? _syncText;

    /// <summary>Upstream branch for the tooltip ("origin/main"), or null.</summary>
    [ObservableProperty] private string? _upstream;

    /// <summary>Last refresh error (e.g. "not a git repository any more"), or null.</summary>
    [ObservableProperty] private string? _error;

    /// <summary>Changed files (the Local Changes section).</summary>
    public LocalChangesViewModel LocalChanges { get; }

    /// <summary>Panel widths (the splitters bind to these).</summary>
    public RepositoryTabLayout Layout { get; }

    /// <summary>
    /// The sidebar tree (Fork-style): Local Changes, All Commits, Branches and Remotes (folding
    /// open to their branches, in folders), Tags, Stashes, Submodules.
    /// </summary>
    public IReadOnlyList<SidebarNode> Sidebar { get; }

    private RepositorySection LocalChangesSection { get; } = new("Changes", IconKind.Edit, "");
    private RepositorySection AllCommitsSection { get; } = new("All Commits", IconKind.Commit, "");
    private RepositorySection TagsSection { get; } = new("Tags", IconKind.Template, "");
    private RepositorySection StashesSection { get; } = new("Stashes", IconKind.Copy, "");
    private RepositorySection SubmodulesSection { get; } = new("Submodules", IconKind.Folder, "");

    /// <summary>The Branches group (local branches) and the Remotes group (each remote, its branches).</summary>
    public SidebarGroupNode BranchesGroup { get; }
    public SidebarGroupNode RemotesGroup { get; }

    private readonly SidebarBranchTree _branchTree;

    /// <summary>
    /// The selected sidebar row. A section shows its page; a branch shows All Commits at that
    /// branch's commit (double-click checks it out); groups, remotes and folders just fold.
    /// </summary>
    [ObservableProperty] private SidebarNode? _selectedNode;

    /// <summary>Set while a branch row drives the page, so switching to All Commits doesn't steal the selection.</summary>
    private bool _revealingBranch;

    /// <summary>Set while the selection is put back after a refresh rebuilt the rows (nothing should happen).</summary>
    private bool _restoringNode;

    partial void OnSelectedNodeChanged(SidebarNode? value)
    {
        if (_restoringNode)
            return;
        switch (value)
        {
            case RepositorySection section:
                SelectedSection = section;
                break;
            case SidebarBranchNode branch:
                _revealingBranch = true;
                try
                {
                    SelectedSection = AllCommitsSection;
                }
                finally
                {
                    _revealingBranch = false;
                }
                CommitLog.Reveal(branch.Branch.CommitId);
                break;
        }
    }

    /// <summary>Enter on a sidebar row: check out a branch, act on a hint, or fold / unfold.</summary>
    [RelayCommand]
    private void ActivateNode()
    {
        switch (SelectedNode)
        {
            case SidebarBranchNode branch when branch.CheckoutCommand.CanExecute(null):
                branch.CheckoutCommand.Execute(null);
                break;
            case SidebarHintNode { Command: { } command } when command.CanExecute(null):
                command.Execute(null);
                break;
            case { Children.Count: > 0 } node:
                node.IsExpanded = !node.IsExpanded;
                break;
        }
    }

    /// <summary>Merge / rebase in progress: the banner, Continue / Skip / Abort, resolving conflicts (G7).</summary>
    public ConflictOperations Conflicts { get; }

    /// <summary>Fetch / Update / Push (toolbar, Git actions, Commit and Push).</summary>
    public RemoteOperations Remote { get; }

    /// <summary>History graph + commit details (the All Commits section).</summary>
    public CommitLogViewModel CommitLog { get; }

    /// <summary>Fingerprint of HEAD and the branch tips from the last refresh: when it changes, the history is reloaded.</summary>
    private string _historySignature = "";

    /// <summary>The Tags section.</summary>
    public TagListViewModel Tags { get; }

    /// <summary>The Stashes section (and Git → Stash Changes…).</summary>
    public StashListViewModel Stashes { get; }

    /// <summary>The Submodules section.</summary>
    public SubmoduleListViewModel Submodules { get; }

    /// <summary>The title-bar branch popup (click the branch, or Ctrl+Shift+`).</summary>
    public BranchPopupViewModel BranchPopup { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentContent))]
    private RepositorySection _selectedSection;

    /// <summary>
    /// What the content area shows: a section's ViewModel once it exists, else the section
    /// itself (the view has a placeholder template for <see cref="RepositorySection"/>).
    /// </summary>
    public object CurrentContent =>
        SelectedSection == LocalChangesSection ? LocalChanges
        : SelectedSection == AllCommitsSection ? CommitLog
        : SelectedSection == TagsSection ? Tags
        : SelectedSection == StashesSection ? Stashes
        : SelectedSection == SubmodulesSection ? Submodules
        : SelectedSection;

    // The history is loaded the first time All Commits is shown (most visits are to Local Changes).
    partial void OnSelectedSectionChanged(RepositorySection value)
    {
        if (value == AllCommitsSection)
            _ = CommitLog.EnsureLoadedAsync(_historySignature);
        // Shown from code (Ctrl+K, Unstash, conflicts…): the sidebar follows. Not while a branch
        // row is what put All Commits there (that row stays selected).
        if (!_revealingBranch && SelectedNode != value)
            SelectedNode = value;
    }

    /// <summary>
    /// Open Terminal (header button, Git → Open Terminal, Alt+F12 as in JetBrains): Git Bash in
    /// this repository's folder, for anything DevHub doesn't do itself.
    /// </summary>
    [RelayCommand]
    public void OpenTerminal() => _terminal.Open(Path);

    /// <summary>Git → New Branch…: from the current commit.</summary>
    public Task NewBranchAsync() => _branchOperations.NewBranchAsync();

    /// <summary>Re-reads the repository status. Called on open, activation, file changes and Git → Refresh.</summary>
    public Task RefreshAsync()
    {
        if (_client is null || _disposed)
            return Task.CompletedTask;

        // A newer refresh replaces an older one still running.
        // (Not disposed: the superseded refresh may still be using its token.)
        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();
        return _refreshTask = RefreshCoreAsync(_client, cts.Token);
    }

    private async Task RefreshCoreAsync(GitRepositoryClient client, CancellationToken ct)
    {
        try
        {
            var status = await client.GetStatusAsync(ct);
            var operation = await client.GetOperationAsync(ct);

            Branch = status.DisplayBranch;
            Upstream = status.Upstream;
            SyncText = (status.Ahead, status.Behind) switch
            {
                (0, 0) => null,
                (var a, 0) => $"↑{a}",
                (0, var b) => $"↓{b}",
                var (a, b) => $"↑{a} ↓{b}",
            };
            Conflicts.Update(operation, status);

            var branches = await client.GetBranchesAsync(ct);
            _branchOperations.Branches = branches;
            BranchPopup.Update(branches);
            var remotes = await client.GetRemoteDetailsAsync(ct);
            var selectedKey = SelectedNode?.Key;
            if (_branchTree.Update(BranchesGroup, RemotesGroup, branches, remotes, Remote.ManageRemotesCommand, Remote.FetchCommand)
                && SelectedNode is not RepositorySection && selectedKey is not null)
            {
                // The rows are new objects: select the same branch / folder again.
                var again = SidebarBranchTree.Descendants(BranchesGroup).Concat(SidebarBranchTree.Descendants(RemotesGroup))
                    .FirstOrDefault(n => n.Key == selectedKey);
                // Only re-select: don't jump All Commits back to the branch on every refresh.
                _restoringNode = true;
                try
                {
                    SelectedNode = again ?? SelectedSection;
                }
                finally
                {
                    _restoringNode = false;
                }
            }

            var tags = await client.GetTagsAsync(ct);
            _commitOperations.Tags = tags;
            Tags.Update(tags);
            TagsSection.Count = tags.Count;

            var stashes = await client.GetStashesAsync(ct);
            Stashes.Update(stashes);
            StashesSection.Count = stashes.Count;

            // No .gitmodules: no git call at all (most repositories).
            var submodules = await client.GetSubmodulesAsync(ct);
            Submodules.Update(submodules);
            SubmodulesSection.Count = submodules.Count;

            // History only changes when HEAD, a branch or a tag moves (commit, checkout, fetch, tag…), not on file edits.
            _historySignature = status.CommitId + "|" + string.Join(",", branches.Select(b => b.Ref + ":" + b.CommitId))
                                + "|" + string.Join(",", tags.Select(t => t.Name + ":" + t.CommitId));
            _ = CommitLog.RefreshIfChangedAsync(_historySignature);

            LocalChanges.Update(status);
            LocalChangesSection.Count = status.Changes.Count;
            IsDirty = status.Changes.Count > 0;
            Error = null;
        }
        catch (OperationCanceledException)
        {
            // Superseded or the tab was closed.
        }
        catch (Exception ex)
        {
            // Started fire-and-forget (constructor, activation, watcher): an exception escaping
            // here would vanish unseen, so show it on the tab instead (git failure, folder gone, …).
            Error = ex.Message;
        }
    }

    protected override void OnActivationChanged(bool isActive)
    {
        // Skip when a refresh is already running (e.g. the one started when the tab was created):
        // starting another would only cancel it and run git twice.
        if (isActive && _refreshTask.IsCompleted)
            _ = RefreshAsync();
    }

    /// <summary>Ctrl+K (JetBrains' Commit): shows Local Changes with the cursor in the commit message.</summary>
    public void FocusCommit()
    {
        SelectedSection = LocalChangesSection;
        LocalChanges.Commit.RequestFocus();
    }

    /// <summary>Ctrl+Alt+Z (JetBrains' Rollback): discard the selected unstaged file, after asking.</summary>
    public Task DiscardSelectionAsync()
    {
        SelectedSection = LocalChangesSection;
        return LocalChanges.DiscardSelectionAsync();
    }

    /// <summary>Git → Stash Changes…: the Stashes section's dialog, from anywhere.</summary>
    public Task StashChangesAsync() => Stashes.StashChangesCommand.ExecuteAsync(null);

    /// <summary>Git → Unstash Changes…: shows the Stashes section (apply or pop one there).</summary>
    public void ShowStashes() => SelectedSection = StashesSection;

    /// <summary>The diff pane's Blame: a blame tab for the file (commit id null = as on disk).</summary>
    private void OpenBlame(string file, string? commitId) =>
        OpenDocument?.Invoke(BlameDocumentFactory.IdFor(Path, file, commitId));

    /// <summary>"Open in New Tab" in the diff pane: the file's diff as its own tab, for more room.</summary>
    private void OpenDiffInTab(FileChangeViewModel file) =>
        OpenDocument?.Invoke(DiffDocumentFactory.IdFor(Path, file.Change.Path, file.IsStagedSide));

    // Raised on a background thread; ViewModel properties must change on the UI thread.
    private void OnRepositoryChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => _ = RefreshAsync());

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_watcher is not null)
        {
            _watcher.Changed -= OnRepositoryChanged;
            _watcher.Dispose();
        }
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
    }
}
