using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Controls;
using DevHub.ViewModels.Documents;
using DevHub.ViewModels.Git;

namespace DevHub.Actions.Providers;

/// <summary>
/// Git actions. They act on the **active repository** (<see cref="ActiveRepositoryService.Repository"/>: the
/// repository tab in front, or the one a diff / blame tab in front belongs to) and are disabled otherwise: commit, push / update / fetch, stash, branches, refresh;
/// more join milestone by milestone (docs/GIT_CLIENT_PLAN.md).
/// </summary>
public sealed class GitActionProvider : IActionProvider, IDisposable
{
    private readonly ActiveRepositoryService _active;
    private readonly AsyncRelayCommand _refresh;
    private readonly RelayCommand _commit;
    private readonly AsyncRelayCommand _discard;
    private readonly RelayCommand _branches;
    private readonly AsyncRelayCommand _newBranch;
    private readonly AsyncRelayCommand _fetch;
    private readonly AsyncRelayCommand _pull;
    private readonly AsyncRelayCommand _push;
    private readonly AsyncRelayCommand _forcePush;
    private readonly AsyncRelayCommand _stash;
    private readonly RelayCommand _unstash;
    private readonly AsyncRelayCommand _manageRemotes;
    private readonly AsyncRelayCommand _publish;
    private readonly AsyncRelayCommand _clone;
    private readonly AsyncRelayCommand _openRepository;
    private readonly RelayCommand _openTerminal;

    private readonly IRemoteHost? _host;

    /// <param name="host">The hosting plugin's service (GitHub), or null: then there's no Publish action.</param>
    public GitActionProvider(ActiveRepositoryService active, CloneOperations clone, OpenRepositoryOperation openRepository,
        IRemoteHost? host = null)
    {
        _active = active;
        _host = host;
        // Clone and Open Repository need no repository: always available.
        _clone = new AsyncRelayCommand(clone.CloneAsync);
        _openRepository = new AsyncRelayCommand(openRepository.OpenAsync);
        _refresh = new AsyncRelayCommand(
            () => _active.Repository!.RefreshAsync(),
            () => _active.Repository is not null);
        _commit = new RelayCommand(
            () => Shown().FocusCommit(),
            () => _active.Repository is not null);
        // Enabled whenever a repository tab is active (not only with a selection), so the key
        // always answers: it discards, or the status bar says what to select.
        _discard = new AsyncRelayCommand(
            () => Shown().DiscardSelectionAsync(),
            () => _active.Repository is not null);
        _branches = new RelayCommand(
            () => _active.Repository!.BranchPopup.Open(),
            () => _active.Repository is not null);
        _newBranch = new AsyncRelayCommand(
            () => _active.Repository!.NewBranchAsync(),
            () => _active.Repository is not null);
        _stash = new AsyncRelayCommand(
            () => _active.Repository!.StashChangesAsync(),
            () => _active.Repository is not null);
        _unstash = new RelayCommand(
            () => Shown().ShowStashes(),
            () => _active.Repository is not null);

        // The active tab's own commands (the same ones its toolbar buttons use). While one is
        // already running there, they do nothing (their CanExecute is false).
        _fetch = RemoteCommand(r => r.FetchCommand);
        _pull = RemoteCommand(r => r.PullCommand);
        _push = RemoteCommand(r => r.PushCommand);
        _forcePush = RemoteCommand(r => r.ForcePushCommand);
        _openTerminal = new RelayCommand(
            () => _active.Repository!.OpenTerminal(),
            () => _active.Repository is not null);
        _manageRemotes = RemoteCommand(r => r.ManageRemotesCommand);
        _publish = RemoteCommand(r => r.PublishCommand);

        // Re-check "is a repository tab active?" when the active tab changes. Unsubscribed in Dispose.
        _active.PropertyChanged += OnActiveChanged;
    }

    public IEnumerable<AppAction> GetActions()
    {
        // Ctrl+K = JetBrains' Commit: go to the commit box; Ctrl+Enter there commits.
        yield return new AppAction(GitActionIds.Commit, "Commit…", _commit)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 0,
            IconKind = IconKind.Commit,
            Gesture = "Ctrl+K",
            Description = "Show Changes of the active repository with the cursor in the commit message.",
        };

        // Ctrl+Alt+Z = JetBrains' Rollback. Window-wide, so it works wherever the focus is.
        yield return new AppAction(GitActionIds.Discard, "Discard Changes…", _discard)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 100,
            IconKind = IconKind.Delete,
            Gesture = "Ctrl+Alt+Z",
            Description = "Throw away the unstaged changes of the file selected in Changes (asks first).",
        };

        // Ctrl+Shift+` = JetBrains' Branches popup (the key left of 1 on US keyboards).
        yield return new AppAction(GitActionIds.Branches, "Branches…", _branches)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 200,
            IconKind = IconKind.Vcs,
            Gesture = "Ctrl+Shift+OemTilde",
            Description = "Search the active repository's branches and check one out.",
        };

        // JetBrains: Push… = Ctrl+Shift+K, Update Project… = Ctrl+T.
        yield return new AppAction(GitActionIds.Push, "Push…", _push)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 1,
            IconKind = IconKind.Push,
            Gesture = "Ctrl+Shift+K",
            Description = "Push the current branch of the active repository (shows the commits first).",
        };

        yield return new AppAction(GitActionIds.Pull, "Update", _pull)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 2,
            IconKind = IconKind.Update,
            Gesture = "Ctrl+T",
            Description = "Pull the current branch's new commits (fast-forward; offers a rebase if both sides changed).",
        };

        yield return new AppAction(GitActionIds.Fetch, "Fetch", _fetch)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 3,
            IconKind = IconKind.Fetch,
            Description = "Download what's new on every remote of the active repository; changes nothing locally.",
        };

        yield return new AppAction(GitActionIds.ForcePush, "Force Push…", _forcePush)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 4,
            IconKind = IconKind.Push,
            Description = "Overwrite the remote branch with yours (after amend / rebase). Asks first; --force-with-lease.",
        };

        // JetBrains: Git → Uncommitted Changes → Stash Changes… / Unstash Changes….
        yield return new AppAction(GitActionIds.Stash, "Stash Changes…", _stash)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 101,
            IconKind = IconKind.Copy,
            Description = "Put the uncommitted changes of the active repository aside (asks for a message first).",
        };

        yield return new AppAction(GitActionIds.Unstash, "Unstash Changes…", _unstash)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 102,
            IconKind = IconKind.Copy,
            Description = "Show the stashes of the active repository, to apply or pop one.",
        };

        // JetBrains: Git → Manage Remotes…, Share Project on GitHub, Get from Version Control….
        yield return new AppAction(GitActionIds.ManageRemotes, "Manage Remotes…", _manageRemotes)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 300,
            IconKind = IconKind.Fetch,
            Description = "Add a remote (name + URL) to the active repository, change its URL, rename or remove it.",
        };

        // Only with a hosting plugin (GitHub): "Git.PublishToGitHub", "Publish to GitHub…".
        if (_host is not null)
            yield return new AppAction(GitActionIds.PublishPrefix + _host.Name, $"Publish to {_host.Name}…", _publish)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 301,
            IconKind = IconKind.Push,
            Description = $"Create a repository on your {_host.Name} account, add it as a remote and push the current branch.",
        };

        yield return new AppAction(GitActionIds.OpenRepository, "Open Repository…", _openRepository)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 302,
            WelcomeOrder = 45,
            IconKind = IconKind.Vcs,
            Description = "Choose a folder and open its git repository as a tab.",
        };

        yield return new AppAction(GitActionIds.Clone, "Clone Repository…", _clone)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 303,
            WelcomeOrder = 46,
            IconKind = IconKind.Fetch,
            Description = "Clone one of your GitHub repositories (or any URL) and open it as a repository tab.",
        };

        yield return new AppAction(GitActionIds.NewBranch, "New Branch…", _newBranch)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 201,
            IconKind = IconKind.Add,
            Description = "Create a branch from the current commit in the active repository.",
        };

        // Alt+F12 = JetBrains' Terminal.
        yield return new AppAction(GitActionIds.OpenTerminal, "Open Terminal", _openTerminal)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 400,
            IconKind = IconKind.Terminal,
            Gesture = "Alt+F12",
            Description = "Open Git Bash (or Windows Terminal / PowerShell) in the active repository's folder.",
        };

        // Ctrl+Alt+Y = JetBrains' "Synchronize" (F5 is already Rescan Repositories).
        yield return new AppAction(GitActionIds.Refresh, "Refresh Repository", _refresh)
        {
            Category = "Git",
            Menu = "Git",
            MenuOrder = 401,
            IconKind = IconKind.Refresh,
            Gesture = "Ctrl+Alt+Y",
            Description = "Re-read the active repository tab (branch, and later changes).",
        };
    }

    /// <summary>
    /// The active repository, with its tab brought to the front: for actions that show something
    /// in it (commit box, Stashes) when a diff or blame tab of that repository is in front.
    /// </summary>
    private RepositoryDocumentViewModel Shown() => _active.BringToFront();

    private AsyncRelayCommand RemoteCommand(Func<RemoteOperations, IAsyncRelayCommand> pick) =>
        new(async () =>
            {
                if (_active.Repository?.Remote is { } remote && pick(remote) is { } command && command.CanExecute(null))
                    await command.ExecuteAsync(null);
            },
            () => _active.Repository is not null);

    private void OnActiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveRepositoryService.Repository))
        {
            _refresh.NotifyCanExecuteChanged();
            _commit.NotifyCanExecuteChanged();
            _discard.NotifyCanExecuteChanged();
            _branches.NotifyCanExecuteChanged();
            _newBranch.NotifyCanExecuteChanged();
            _fetch.NotifyCanExecuteChanged();
            _pull.NotifyCanExecuteChanged();
            _push.NotifyCanExecuteChanged();
            _forcePush.NotifyCanExecuteChanged();
            _stash.NotifyCanExecuteChanged();
            _manageRemotes.NotifyCanExecuteChanged();
            _openTerminal.NotifyCanExecuteChanged();
            _publish.NotifyCanExecuteChanged();
            _unstash.NotifyCanExecuteChanged();
        }
    }

    public void Dispose() => _active.PropertyChanged -= OnActiveChanged;
}
