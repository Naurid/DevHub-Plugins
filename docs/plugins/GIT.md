# Git (`devhub.git`)

A Fork-style Git client with JetBrains' integration: one editor tab per repository with changes, staging per line,
commits, a history graph, branches, remotes, merge and rebase, stashes, tags, blame and submodules. Bundled with
DevHub. Requires nothing; the git CLI must be installed (without it, repository tabs explain how to get it).

Code: `plugins/Git/DevHub.Git` and `DevHub.Git.Core` (the git runner, parsers for status, diff, log, blame, the patch
builder, the graph layout; tested against real git in temp repositories in `tests/DevHub.Git.Core.Tests`). The design
and its rules: `docs/GIT_CLIENT_PLAN.md` and this repository's `CLAUDE.md`.

## Using it

**Open a repository**: from the Projects tool window (double-click), Git → Open Repository…, Clone Repository…, or a
restored tab. The tab title shows a dot while there are uncommitted changes.

**The repository tab**

- **Sidebar**: Changes (with a count), All Commits, Local branches (folders by "/", ahead / behind), Remotes and their
  branches, Tags, Stashes, Submodules. Double-click a branch to check it out; right-click for its menu (merge, rebase,
  rename, delete, new branch, push…).
- **Changes**: Unstaged and Staged lists (tree or flat, saved), stage / unstage with double-click, Space or the
  buttons; per hunk and per line in the diff pane; discard (confirmed). The commit box: message, Amend, Commit,
  Commit and Push; failing hooks show their output. Suggestions from other plugins (Board Git's "DEV-12: ") appear
  above the box, never filled in silently.
- **Diffs**: unified or side by side (one saved switch), Open in Editor (**F4**) at the first changed line, the diff in
  its own tab, Blame.
- **All Commits**: the graph and list of every branch (1,000 at a time, Load more), search, the selected commit's
  files and diffs; cherry-pick, revert, new branch or tag here. Marks from other plugins (`IGitDecorator`).
- **Merge, rebase, cherry-pick, revert in progress**: a banner with Continue, Skip (rebase), Abort (confirmed);
  conflicted files first, Accept Yours / Theirs, Open in Merge Tool, Mark as Resolved.
- **Remotes**: Fetch, Update (fast-forward, offers a rebase when both sides moved), Push (shows outgoing commits,
  publishes a new branch with upstream, offers Rebase and Push when rejected), Force Push (`--force-with-lease`,
  confirmed). Each runs in the status bar with its progress and Cancel; failures are explained (authentication, SSH
  host key, network, rejected, diverged…).
- **Open Terminal** (**Alt+F12**): Git Bash in the repository, else Windows Terminal, else PowerShell.
- **Narrow windows**: the sidebar (at least 200 px) and the changes list (at least 260 px) always stay side by side;
  when there's no room left for the diff, the diff is hidden (also in commit details), and the window doesn't get
  narrower than the two lists plus the open tool windows.

**Around the tabs**: the title-bar branch widget (the front tab's branch; click for the branch popup), the **Git
Console** tool window (bottom: every git command DevHub ran, with its duration, exit code and output).

## Actions

All act on the repository of the tab in front (also from its diff and blame tabs) and are disabled without one.

| Id | Text | Shortcut |
|---|---|---|
| `Git.Commit` | Commit… | Ctrl+K |
| `Git.Discard` | Discard Changes… | Ctrl+Alt+Z |
| `Git.Branches` | Branches… | Ctrl+Shift+` |
| `Git.NewBranch` | New Branch… | |
| `Git.Push` | Push… | Ctrl+Shift+K |
| `Git.Pull` | Update | Ctrl+T |
| `Git.Fetch` | Fetch | |
| `Git.ForcePush` | Force Push… | |
| `Git.Stash` / `Git.Unstash` | Stash Changes… / Unstash Changes… | |
| `Git.ManageRemotes` | Manage Remotes… | |
| `Git.PublishTo<Host>` | Publish to GitHub… (one per `IRemoteHost`) | |
| `Git.OpenRepository` | Open Repository… (Welcome) | |
| `Git.Clone` | Clone Repository… (Welcome) | |
| `Git.OpenTerminal` | Open Terminal | Alt+F12 |
| `Git.Refresh` | Refresh Repository | Ctrl+Alt+Y |

Commit and Push is Ctrl+Alt+K inside the commit box. All are in the Git menu and Find Action.

## Settings (`devhub.git`)

| Field | Default | Meaning |
|---|---|---|
| `ChangesAsTree` | true | Changes shows folders rather than a flat list. |
| `DiffSideBySide` | false | Diffs side by side rather than unified. |

Panel widths are saved with the tab layout (`RepositoryTabLayout`).

## For other plugins

**Offers** (`DevHub.Integration`):

| Hook | To |
|---|---|
| `IProjectOpener`: open a folder as a repository tab | Projects, Templates |
| `IRepositoryEvents`: `BranchChanged`, `Committed` (also commits made outside DevHub while the tab is open), `Pushed`, `Fetched` | Board Git |

**Uses** (optional): `IProjectFileEditor` and `IProjectFolders` (Projects), `IAddFilesFromTemplate` (Templates),
`ICommitMessageSuggester` (Board Git) and `IGitDecorator` (Board Git: issue keys; CI: ✓ ✗ ● run marks).

**Its own extension point, for plugins that require Git: `IRemoteHost`** (`DevHub.Git`). A hosting service Git can use
without knowing it; GitHub implements it.

| Member | Meaning |
|---|---|
| `Name` | "GitHub", in labels ("Publish to GitHub…", "Your GitHub repositories"). |
| `IsAvailable` | The host can be used at all. |
| `CreateRepositoryAsync(suggestedName, existingRemotes)` | Creates an empty repository for Publish (its own dialog); `CreatedHostRepository(RemoteName, CloneUrl, WebUrl)` or null. |
| `GetMyIdentityAsync()` | `HostIdentity(Names, Avatar, IsOwnEmail)`: your picture next to your commits. |
| `ListAccountsAsync()`, `SignInAsync()` | Accounts, sign-in. |
| `ListRepositoriesAsync(account, ct)` | `HostRepository` list for Clone. |

**Services for plugins that require Git** (Board Git): `GitRunner` (runs git, logs every command to the Git Console;
registered only when git is installed), `GitPlugin.Executable`.

## Diagrams

`docs/diagrams/Git/`: use cases, the activity of staging, committing and pushing (with the rejected-push path), and the classes.
