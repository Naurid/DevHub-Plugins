# Integrated Git client — plan

A Git client inside DevHub that mixes **Fork's workflow** with **JetBrains' integration**,
styled like JetBrains (the UI-0 design system). **Each repository opens in its own editor
tab**, so several repositories can be worked on side by side and switched like files.
Milestones and checkboxes live in `ROADMAP.md` ("G — Integrated Git client"). Built on the
UI-0 shell, so most screens compose existing pieces (see `UI_GUIDE.md`).

---

## 1. Decisions (confirmed 2026-10-09)

| Decision | Choice |
|---|---|
| Engine | **git CLI** (run `git.exe`, parse its machine-readable output), not LibGit2Sharp. See §2. |
| Functionality | **Fork + JetBrains mix:** Fork's per-repository workspace (sidebar of branches/remotes/tags/stashes, Local Changes staging view, commit graph); JetBrains' integration (Find Action, shortcuts, title-bar branch widget, status bar, Git Console tool window). |
| Look | **JetBrains** (UI-0 tokens and components); no Fork-specific styling. |
| Multiple repositories | **One editor tab per repository**, using the existing tab system; tabs are restored on restart. |

---

## 2. Why the git CLI

| | **git CLI** | LibGit2Sharp |
|---|---|---|
| Features | everything git can do, always current | subset; some operations missing or behind |
| Auth (HTTPS, SSH) | your existing credential manager, SSH keys and config | SSH not supported in the default build |
| Behaviour | identical to the terminal; hooks and config respected | can differ subtly from the CLI |
| Speed | a process per command (~10–50 ms); fine for UI actions | in-process |
| Dependency | Git installed (detectable; 2.47 on the dev machine) | native binaries to ship |

Fork, GitHub Desktop and JetBrains also drive the CLI. It fits DevHub's process rule
(`ProcessStartInfo` + `ArgumentList`, never a shell string). Output is read only from
machine formats: `--porcelain=v2 -z`, `--format` with NUL separators, `-z` for paths.

---

## 3. What it looks like

### The repository tab (Fork's workspace, JetBrains' look)

Opened from the Projects tool window (double-click / Enter / "Open Repository"), or
restored at startup. Tab title: repository name, with a dot when there are uncommitted
changes; tooltip shows the path and branch.

```
┌ Repositories ┐┌─ DevHub ● ─┬─ Timber ─┬─ Templates ─┐                                  ┐
│ (tool window)││ ┌ Sidebar ──────┐┌ Local Changes ────────────────────────────────────┐ │
│  P           ││ │ ▸ Local Changes 3││ Unstaged (2)        │ diff of selected file       │ │
│   DevHub     ││ │ ▸ All Commits    ││  M App.axaml        │  - old line                 │ │
│   Timber     ││ │ Branches         ││  ? Notes.md         │  + new line                 │ │
│              ││ │   ● main  ↑1     ││ Staged (1)          │                             │ │
│              ││ │     feature/x    ││  A Icon.cs          │                             │ │
│              ││ │ Remotes          ││ ─────────────────── │                             │ │
│              ││ │   origin         ││ Commit message…     │                             │ │
│              ││ │ Tags · Stashes   ││ [ ] Amend   [Commit]│                             │ │
│              ││ └──────────────────┘└───────────────────────────────────────────────────┘ │
└──────────────┘└─────────────────────────────────────────────────────────────────────────┘
 Git Console (bottom tool window): every command DevHub ran, per repo        main ↑1 ↓0
```

- **Sidebar** (inside the tab, like Fork): Local Changes (with count), All Commits,
  Branches (local; current marked; ahead/behind), Remotes (with their branches), Tags,
  Stashes, later Submodules. Built from `TreeView` + `Badge` + `SearchField`.
  *As built:* a `TreeView` of `SidebarNode`s (`ViewModels/Documents/RepositorySidebar.cs`).
  Branches and Remotes (→ each remote, URL in its tooltip) fold open to their branches, put in
  folders by "/" (`Core/Formatting/PathTree`, tested). Selecting a branch shows All Commits at its
  commit (`CommitLog.Reveal`), double-click / Enter checks it out, right-click has the branch
  menu; the group rows have New Branch… / Manage Remotes… / Publish to GitHub… / Fetch All.
  What's unfolded survives refreshes; folders holding the current branch start unfolded.
  The tree is only rebuilt when the branches or remotes change.
  Labels: **Changes** (was Local Changes), All Commits, **Local** (the branches), Remotes, Tags,
  Stashes, Submodules. No icons; names start at the left edge and the fold arrow sits on the
  right (the `SidebarItem` TreeViewItem theme in `RepositoryDocumentView`, rows indented by level).
  Commit / stash details list their files in folders too (same switch as Changes, `ChangeRows`).
- **Local Changes view** (Fork's staging view): Unstaged and Staged lists (stage by
  double-click / button / Space, unstage the same way, discard with confirmation), a diff
  pane for the selected file, and the commit box (message, Amend, Commit / Commit & Push).
  *Tree view* (Fork's, the "Folders" switch, saved in settings `Git.ChangesAsTree`, on by
  default): the lists show folders with fold arrows (single-child chains merged:
  "src/DevHub.App") as rows in the same ListBox, so multi-select, Space and the menus work as
  before; a selected folder means every file in it. Left / Right fold, double-click toggles.
- **All Commits view** (G5): commit graph + list (subject, author, date, branch/tag badges),
  and below it the selected commit's details and changed files with their diffs.
  *As built:* `git log --all --topo-order` (stashes and notes excluded), 1,000 commits per
  page with "Load more"; lanes laid out in Core (`CommitGraph`, tested) and drawn by
  `CommitGraphCell`; a merge's files are compared with its first parent; loaded the first time
  the section opens, reloaded only when HEAD or a branch tip moves. Search hides the graph
  (with commits left out its lines would be wrong). Not yet: actions on a commit (checkout,
  branch here, copy id, cherry-pick/revert in G8); the id and message are selectable for Ctrl+C.
- **Partial staging** (G3.1): both file lists take Ctrl/Shift+click and Ctrl+A, and Stage /
  Unstage / Discard / Space act on all selected files. In the diff pane each hunk header has
  Stage / Discard / Unstage Hunk, and selected lines get Stage / Discard / Unstage Lines (Space
  toggles). *As built:* `Core/Git/PatchBuilder` turns the chosen lines into a patch for
  `git apply` (`--cached` to stage, `--cached --reverse` to unstage, `--reverse` to discard):
  an unselected change is kept as context on the side git checks and dropped from the other,
  and the other side's hunk starts are recomputed. CRLF lines and "no newline at end of file"
  are kept byte for byte (`DiffLine.HasCarriageReturn`). Not offered for binary, conflicted,
  renamed or cut-off diffs (stage the whole file).
- **Merge / rebase / conflicts** (G7): the branch menu has Merge into Current Branch… and Rebase
  Current Branch onto This… (both confirmed). A merge / rebase / cherry-pick / revert in progress
  shows a banner with what to do next and Continue (enabled once no conflict is left), Skip
  Commit… (rebase only) and Abort… (confirmed: resolutions are lost). Conflicted files are first
  in Unstaged, in red; their menu has Accept Yours… / Accept Theirs… (DevHub maps these to git's
  `--ours` / `--theirs`, which swap meaning during a rebase), Open in Merge Tool (the user's
  `merge.tool`, waited for in the status bar with Cancel; double-click does the same; without a
  tool, offers the editor) and Mark as Resolved (= Space; warns if conflict markers are left).
  *As built:* `GitRepositoryClient` Merge/Rebase/Continue/Skip/Abort/Resolve return an
  `OperationOutcome` (Done / Conflicts / UpToDate); `ViewModels/Git/ConflictOperations`.
- **Stashes, tags, cherry-pick / revert, blame, submodules** (G8). *As built:*
  - **Stashes** section: list (message, branch, "untracked" badge) with the selected stash's
    files and diffs below (the same `CommitDetailsView` as All Commits; untracked files come from
    the stash's third parent). Stash Changes… (message, include untracked, keep staged; also
    Git → Stash Changes…), Apply, Pop (a conflicting pop keeps the stash and shows Local
    Changes), Drop… (confirmed). Stashes are found again by id, since `stash` shifts.
  - **Tags** section: name, "annotated" badge, commit, message, date. New Tag… (on HEAD; on any
    commit from All Commits), Push Tag (to `origin`, or the only remote; Push never sends tags),
    Delete… and Delete from Remote… (both confirmed), New Branch from Here….
  - **All Commits menu**: Cherry-Pick, Revert Commit (merges against their first parent, `-m 1`),
    New Branch from Here…, New Tag Here…. Not confirmed (they add a commit and lose nothing,
    like JetBrains); conflicts use the G7 banner; a cherry-pick whose changes are already there
    is skipped and reported as "nothing to do".
  - **Blame**: the diff pane's Blame button opens a tab (`Blame:` documents) with commit id,
    author and date once per block of lines; the file on disk for a local change (uncommitted
    lines say so), the file in that commit from All Commits / Stashes. Double-click / F4 opens
    the editor at the line.
  - **Submodules** section: path, state (up to date / other commit / not cloned / conflict),
    Update Submodules (`submodule update --init --recursive`, status bar with Cancel), Open
    (a submodule as its own repository tab). No git call when there's no `.gitmodules`.
- **Remotes and GitHub** (G9). *As built:*
  - **Manage Remotes…** (Remotes section header, Git menu): add (name + URL), edit (rename and
    / or new URL), remove (confirmed; the server is untouched). Adding one fetches it at once.
  - **GitHub account = Git Credential Manager** (`Core/GitHub/GitCredentialManager`): Settings →
    GitHub lists GCM's GitHub accounts (each checked with `GET /user`), Sign In… (GCM's own
    window, `credential-manager github login`, the only `Interactive` git command) and Sign Out…
    (confirmed). DevHub stores no credentials: for each API call it borrows the token with
    `git credential fill` (`Sensitive`: the Git Console shows the command, never the output;
    `GCM_INTERACTIVE=never`).
  - **Publish to GitHub…** (Remotes section, Git menu): creates an empty repository on the account
    (`POST /user/repos`, private by default; done inside the dialog so "name taken" shows
    there), adds it as a remote, pushes the current branch with `-u`. A failed later step says
    what already happened.
  - **Push with no remote** offers Publish to GitHub… or Add a remote by URL…, and then pushes.
  - **Avatars:** your GitHub picture in Settings → GitHub and next to your own commits in All Commits
    (recognised by user.email, your login's no-reply address or your name); other authors show
    their initial (no Gravatar: no e-mail hashes sent to a third party). The picture comes from
    `avatars.githubusercontent.com` without the token.
  - **Clone Repository…** (Git menu, Projects tool window): your GitHub repositories
    (`GET /user/repos`, all pages, fuzzy search) or any URL; into a scan folder by default. Runs in
    the status bar with Cancel (a cancelled clone is removed), then opens as a tab and rescans.
- **Fetch / Update / Push** (G6): buttons in the tab's header, the Git menu, Ctrl+T (Update),
  Ctrl+Shift+K (Push…), Ctrl+Alt+K (Commit and Push). *As built* (`ViewModels/Git/RemoteOperations`):
  fetch = `fetch --all --prune`; Update = `pull --ff-only`, and when both sides have new commits
  it offers a rebase (`pull --rebase --autostash`); Push shows the outgoing commits first, publishes
  a branch without upstream to `origin` with `-u`, and on "rejected" offers "Rebase and Push";
  Force Push is a separate, confirmed action using `--force-with-lease`. Each runs in the status
  bar (step + progress parsed from `--progress`, Cancel kills git), one at a time per repository.
  Failures are recognised in Core (`RemoteErrorExplainer`: auth, SSH host key, network, repository
  not found, rejected, no upstream, diverged, local changes in the way, conflicts) and explained in
  a dialog with git's own lines below. git's stdin is closed, so ssh can't hang waiting for a
  passphrase nobody can type; credential-manager windows still appear as usual.
- Each tab keeps its own selection, scroll and commit message; switching tabs is instant.

### JetBrains integration around it

| Piece | Behaviour |
|---|---|
| **Title-bar branch widget** | shows the **active repository tab's** branch; click → branch popup (search with FuzzyMatcher, checkout, new branch, …). Hidden when the active tab isn't a repository. |
| **Status bar** | branch + ahead/behind of the active repo; progress of running operations (fetch/push) with cancel |
| **Actions** (menus, Find Action, shortcuts) | act on the active repository: Commit (Ctrl+K), Push (Ctrl+Shift+K), Update/Pull (Ctrl+T), Fetch, New Branch, Checkout…, Show Diff, Refresh, Open Repository. Disabled when no repository tab is active. |
| **Git Console** (bottom tool window) | every git command DevHub ran: repository, command, duration, exit code, output on expand |
| **Projects tool window** (code: `RepositoriesViewModel`) | the launcher: opens repository tabs; later shows branch and a dirty marker per repository |
| **Open in Editor** (F4) | the real file in the external editor tied to the repository's language (Settings → Open With: Rider / VS Code at the first changed line, Visual Studio, …), like JetBrains' Jump to Source. "Open in New Tab" shows the diff as its own tab for more room |

New tokens: `Brush.Diff.Added/Removed/Modified` (+ line backgrounds) and graph lane colors,
Dark and Light. New expui icons (branch, tag, stash, pull, history, …) added to NOTICE.md.

---

## 4. Architecture

```
DevHub.Core/Git/                       (no Avalonia; unit-tested)
  GitExecutable        find git.exe (PATH, Git for Windows default), check version >= 2.30
  GitRunner            run one command: ArgumentList, working dir, stdin, cancellation,
                       streamed stderr (progress), exit code → result / GitException
  GitCommandLog        every command + repo + duration + exit code (Git Console)
  Parsers/             Status (porcelain v2), Diff (unified), Log (commits + parents),
                       Refs (for-each-ref), Progress
  GitRepositoryClient  per-repository API: GetStatusAsync, StageAsync, CommitAsync, …
  Models               RepoStatus, FileChange, Diff/Hunk/Line, Commit, Branch, Remote, …

DevHub.App/
  ViewModels/Documents/RepositoryDocumentViewModel   one per open repository (the tab):
                       owns its GitRepositoryClient, watcher, sidebar, current view
  ViewModels/Git/      LocalChangesViewModel, CommitGraphViewModel, BranchesViewModel, …
  Services/IActiveRepository   the repository of the active tab (or null) + Changed
  ToolWindows/GitConsoleViewModel (bottom)
  Dialogs/             New Branch, Push, Confirm destructive
  Actions/Providers/GitActionProvider
```

### Multi-instance documents (needed first, in G0)

Today `DocumentManager` only opens the DI singletons (one tab per document type, `Id` = type
name). Repository tabs need **many instances of one document type**, each with its own id,
restored on restart:

- `DocumentManager.Show(document)` accepts documents that aren't in `Available` (created
  on demand); `Close` removes them (and disposes them: stop the watcher).
- Ids for instance documents: `"Repository:" + full path`. Opening an already-open repository
  activates its tab instead of opening a second one.
- **Restoring:** an `IDocumentFactory` per id prefix (`"Repository:"` → creates a
  `RepositoryDocumentViewModel` for that path, if it's still a git repo). `EditorLayout`
  already stores ids in tab order, so it needs no change.
- Singleton documents (Templates, New Project, …) keep working as they are.

**Active repository.** `IActiveRepository` follows `DocumentManager.Active`: when it's a
repository tab, that repository; otherwise null. Git actions, the title-bar branch widget
and the status bar read it. This is the "data context" the actions doc lists as missing.

**Refreshing** (as built in G1). Each open repository tab has a `RepositoryWatcher`
(`FileSystemWatcher`, debounced 300 ms) that ignores `.git/` except `index`, `HEAD`, `refs/`,
`packed-refs`, the merge/cherry-pick/revert markers and `rebase-*`. A tab refreshes when it is
created, when it becomes active (unless a refresh is already running), and on its watcher's
events, **background tabs included**: the debounce plus the watcher only firing for that
repository keeps this cheap (one `git status` per repository per burst of changes), and every
tab keeps an accurate dirty dot. Later milestones also refresh after every command DevHub runs.
Status runs with `GIT_OPTIONAL_LOCKS=0`, so it never fights a terminal git and never
re-triggers its own watcher.

---

## 5. Rules (added to CLAUDE.md when G0 lands)

1. **Run git only via `GitRunner`:** `ArgumentList`, `UseShellExecute = false`, never a
   concatenated command line. Environment: `GIT_TERMINAL_PROMPT=0` (never hang on a password
   prompt); `GIT_OPTIONAL_LOCKS=0` for read-only commands.
2. **Parse only machine formats** (`--porcelain=v2`, `-z`, explicit `--format`), never human
   output (it changes with version and language).
3. **Destructive operations need confirmation** saying exactly what will be lost: discard
   changes, delete an unmerged branch, reset, force push (only `--force-with-lease`).
4. **Show every command** in the Git Console.
5. **Read-only work may happen on its own; anything that changes a repository only on a
   user action.**
6. **Async + cancellable**; long operations stream progress and can be cancelled.
7. **One repository per tab, one `GitRepositoryClient` per repository.** A command always
   names its repository; no global "current directory".
8. **Paths in Core are git paths** (forward slashes, relative to the repo root), converted at
   the UI boundary.
9. **Credentials only from Git Credential Manager.** DevHub never asks for, stores or logs a
   password or token: it borrows GCM's token in memory for one API call (`git credential fill`
   with `Sensitive`), and API errors never include it. GitHub API calls only go to
   `api.github.com` (paging links elsewhere are not followed).

---

## 6. Milestones

| | Milestone | Done when |
|---|---|---|
| **G0** | **Foundation + repository tabs:** `GitExecutable`, `GitRunner`, `GitCommandLog` (Core, tested with real git in temp repos); multi-instance documents (instance ids, factory, restore); `RepositoryDocumentViewModel` with an empty sidebar + header (name, path, branch); open from the Repositories tool window; `IActiveRepository`; read-only branch widget in the title bar; Git Console tool window | open three repos as tabs, restart → they come back; the title bar shows the active tab's branch; the Console lists the commands run |
| **G1** | **Status:** porcelain v2 parser (branch, upstream, ahead/behind, ordinary/renamed/unmerged/untracked, merge/rebase in progress); watcher refresh; **Local Changes view** read-only (Unstaged/Staged lists); change count in the sidebar and a dirty dot on the tab | parser tests incl. renames, spaces/unicode, conflicts; lists update while editing files |
| **G2** | **Diff:** unified-diff parser (hunks, line numbers, binary, renames, no-newline marker); diff pane in Local Changes (unified first, side-by-side later); "Open in Editor" (F4) opens the file in the language's external editor; "Open in New Tab" diff tab | parser tests; selecting a file shows its diff |
| **G3** | **Stage & commit:** stage/unstage/discard files (discard confirmed), commit message, amend, commit via stdin (`git commit -F -`), Commit & Push placeholder; later per-hunk/line staging (`git apply --cached`) | edit → stage → commit loop tested in Core; Ctrl+K commits in the active repo |
| **G4** | **Branches:** sidebar Branches/Remotes sections (`for-each-ref`), checkout (dirty-tree: stash/cancel), create, rename, delete (unmerged → confirm); title-bar branch popup | switching branches from sidebar and popup; actions in Find Action |
| G5 | **All Commits:** log parser + graph lane layout (Core, tested), virtualized list, commit details + diffs, branch/tag badges, search | this repo's history renders with merges laned correctly |
| G6 | **Remotes:** fetch / pull (`--ff-only` default, rebase option) / push with progress + cancel; auth errors explained | push/pull against a local bare repo in tests |
| G7 | **Merge / rebase / conflicts:** state banner, continue/abort/skip, conflict list, external merge tool, ours/theirs | conflict scenario test |
| G8 | Stashes and Tags sections, cherry-pick, revert, blame, submodules | — |
| G9 | **Remotes & GitHub:** manage remotes; GitHub account via Git Credential Manager (sign in / out); Publish to GitHub; Clone (your repositories or a URL) | remotes + clone against local bare repos; API with a fake HTTP handler |

**First release = G0–G4.** Each milestone ends in something that builds, with Core tests.

---

## 7. Testing

- **Core tests use real git** in temporary repositories through a fixture that fixes
  author/date/config and isolates the user's config (`GIT_CONFIG_GLOBAL`/`GIT_CONFIG_SYSTEM`
  → empty files). A `[GitFact]` attribute skips them when git isn't installed.
- **Parser tests** also use captured output strings for hard-to-reproduce edge cases
  (unusual renames, unicode paths, CRLF, conflicts).
- **UI** is checked in the running app with `DEVHUB_DATA_DIR` set to a scratch folder and
  scratch repositories, never the user's real repos.

---

## 8. Risks / open questions

- **Many open repository tabs:** one watcher each; a tab only refreshes when its own
  repository changes (§4). If many busy repositories ever make this slow, fall back to
  refreshing background tabs on activation only.
- **Big repositories** (Unity): `--untracked-files=normal`, cancellation, virtualized lists,
  paged log.
- **Line endings / encodings** in diffs: show "binary or unsupported encoding" rather than garbage.
- **Unity specifics:** `.meta` files, LFS assets; show them, no special handling yet.
- **Git missing or too old:** a clear empty state with an install link, not errors.
- **Hooks** (pre-commit) can be slow or fail: stream their output to the Console and the commit box.
- **Moved/deleted repositories** in saved tabs: skipped on restore with a status-bar note.
