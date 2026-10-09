# Integrated Git client — plan

A Fork / JetBrains-style Git UI inside DevHub: see what changed, stage, commit, diff,
branches, history, and sync with remotes, for the repositories in the Repositories tool
window. This document is the plan; milestones and checkboxes live in `ROADMAP.md`
("G — Integrated Git client"). Built on the UI-0 shell, so most screens are composition of
existing pieces (tool windows, documents, actions, dialogs; see `UI_GUIDE.md`).

---

## 1. Goals and non-goals

**Goals (first release = G0–G4):**
- Pick a repository; see its branch, ahead/behind, and changed files at a glance.
- Stage / unstage / discard files (later: hunks), write a message, commit (incl. amend).
- A diff viewer for any changed file.
- Switch, create, rename and delete branches.
- Every git command DevHub runs is visible in a log, so nothing happens behind your back.

**Later (G5–G8):** history graph, fetch/pull/push, merge/rebase conflicts, stash, tags,
cherry-pick, blame.

**Non-goals:** replacing the git CLI for power users; implementing git itself; hosting
features (pull requests, issues); LFS management beyond what git already does.

---

## 2. Key decision: git CLI, not a library  *(decision for you to confirm)*

| | **git CLI** (run `git.exe`, parse its output) | **LibGit2Sharp** (libgit2 bindings) |
|---|---|---|
| Features | everything git can do, always current | subset; some operations missing or behind (e.g. sparse, some rebase/merge cases, partial clone) |
| Auth (HTTPS, SSH) | uses your existing credential manager, SSH keys and config | SSH not supported in the default build; credentials must be plumbed manually |
| Behaviour | identical to the terminal, hooks run, `.gitconfig` respected | can differ subtly from CLI (hooks, config, filters) |
| Speed | a process per command (~10–50 ms); fine for UI actions | in-process, faster for many tiny reads |
| Dependency | needs Git installed (DevHub users have it; detectable) | native binaries to ship per platform |

**Recommendation: git CLI**, the same choice Fork, GitHub Desktop and JetBrains make. It
fits DevHub's existing rule for running processes (`ProcessStartInfo` + `ArgumentList`,
never a shell string). Parsing is made safe by using git's machine-readable formats:
`--porcelain=v2 -z`, `--format` with NUL separators, `-z` everywhere paths appear.

---

## 3. Architecture

```
DevHub.Core/Git/                       (no Avalonia; all unit-tested)
  GitExecutable      find git.exe (PATH, Git for Windows default), check version >= 2.30
  GitRunner          run one command: ArgumentList, working dir, stdin, cancellation,
                     streamed stderr (progress), exit code → result / GitException
  GitCommandLog      every command + duration + exit code (for the Git Console)
  Parsers/           StatusParser (porcelain v2), DiffParser (unified diff),
                     LogParser (commits + parents), RefParser (for-each-ref), ProgressParser
  GitRepositoryClient   high-level API: GetStatusAsync, StageAsync, CommitAsync, … (one per repo)
  Models             RepoStatus, FileChange, Diff/Hunk/Line, Commit, Branch, …

DevHub.App/
  Services/IRepositoryContext   "current repository" (selected in Repositories / title bar)
  ToolWindows/      Commit (left), Git log + Console (bottom)
  Documents/        Diff (one tab per file diff)
  Dialogs/          New branch, Push, Confirm-destructive
  Actions/Providers/GitActionProvider   Commit (Ctrl+K), Push (Ctrl+Shift+K), Update (Ctrl+T),
                                        New Branch, Checkout…, Show Diff (Ctrl+D), Refresh
```

**Current repository (`IRepositoryContext`).** A small service holding the active
`GitRepository` and raising `Changed`. Set from the Repositories tool window selection and a
branch/repo widget in the title bar (like JetBrains' VCS widget). Git actions read it, which
is also the "data context" the actions doc lists as a limitation today.

**Refreshing.** A `FileSystemWatcher` per active repo, debounced (~300 ms), ignoring most of
`.git/` except `HEAD`, `index` and `refs/`. Plus refresh on window activation and after every
command DevHub runs. Status uses `--no-optional-locks` so it never fights a terminal git.

---

## 4. Rules (added to CLAUDE.md when G0 lands)

1. **Run git only via `GitRunner`:** `ArgumentList`, `UseShellExecute = false`, never a
   concatenated command line. Environment: `GIT_TERMINAL_PROMPT=0` (never hang waiting for a
   password prompt), `GIT_OPTIONAL_LOCKS=0` for read-only commands.
2. **Parse only machine formats** (`--porcelain=v2`, `-z`, explicit `--format`). Never
   parse human output; it changes with git version and language.
3. **Destructive operations need confirmation** listing exactly what will be lost: discard
   changes, delete branch (unmerged), reset, force push (only `--force-with-lease`).
4. **Show every command** in the Git Console (bottom tool window): command, time, result.
5. **Read-only work happens on its own**; anything that changes the repo happens only on a
   user action.
6. **Async + cancellable** everywhere; long operations (fetch/push) stream progress to the
   status bar and can be cancelled.
7. **Paths are git paths** (forward slashes, relative to the repo root) in Core; converted
   at the UI boundary.

---

## 5. UI (JetBrains layout)

| Piece | Where | Built from |
|---|---|---|
| **Commit** tool window | left, Alt+0 (JetBrains) | `ToolWindowFrame`, tree of changes (Staged / Changes / Untracked), checkboxes, message box, Commit / Amend buttons |
| **Diff** | editor tab | new `DiffView`: side-by-side or unified, line numbers, colored added/removed lines, hunk navigation (F7) |
| **Git** tool window | bottom, Alt+9 | tabs: **Log** (G5, graph + details) and **Console** (command log) |
| **Branch widget** | title bar, after the breadcrumb | `ActionButton` + popup: branches with search (FuzzyMatcher), New Branch, Checkout |
| **Status bar** | right | current branch, ahead/behind badges, running-operation progress |
| **Dialogs** | — | New Branch, Push (target, force-with-lease), Confirm destructive |

New tokens: `Brush.Diff.Added/Removed/Modified` (+ `.Line` backgrounds), Dark and Light.
New icons from the expui set (branch, tag, stash, pull, history…), added to NOTICE.md.

---

## 6. Milestones

| | Milestone | Done when |
|---|---|---|
| **G0** | **Foundation:** `GitExecutable`, `GitRunner`, `GitCommandLog`, `GitException`; test fixture creating temp repos; `IRepositoryContext` + title bar repo/branch widget (read-only) | tests run real `git` in temp repos (skipped if git missing); widget shows the selected repo's branch |
| **G1** | **Status:** porcelain v2 parser (branch, upstream, ahead/behind, ordinary/renamed/unmerged/untracked entries, in-progress merge/rebase), watcher refresh; **Commit tool window** read-only | parser tests incl. renames, spaces/unicode in paths, conflicts; tool window lists changes live |
| **G2** | **Diff:** unified-diff parser (hunks, line numbers, binary, renames, no-newline marker); Diff document (unified first, side-by-side after) | parser tests; double-click a change → diff tab |
| **G3** | **Stage & commit:** stage/unstage/discard files (discard confirmed), commit message, amend, commit via stdin (`git commit -F -`); later per-hunk staging (`git apply --cached`) | full edit → stage → commit loop in a temp repo, tested in Core; Ctrl+K commits |
| **G4** | **Branches:** list (`for-each-ref`), checkout (dirty-tree handling: stash / cancel), create, rename, delete (unmerged → confirm) | branch popup works; actions in Find Action |
| G5 | **History:** log parser + graph lane layout (Core, tested), virtualized list, commit details + diff | log of this repo renders with merges correctly laned |
| G6 | **Remotes:** fetch / pull (`--ff-only` default, choice of rebase) / push with progress parsing and cancel; credential + SSH errors explained | push/pull against a local bare repo in tests |
| G7 | **Merge / rebase / conflicts:** state banner, continue/abort/skip, conflict list, "open in external merge tool", ours/theirs | conflict scenario test |
| G8 | Stash, tags, cherry-pick, revert, blame | — |

**First release = G0–G4.** Each milestone ends in something that builds, with Core tests,
like the template milestones.

---

## 7. Testing

- **Core tests use real git** in temporary repositories (`git init`, write files, commit),
  through a fixture that sets a fixed author/date/config so output is deterministic and the
  user's global config can't interfere (`GIT_CONFIG_GLOBAL`/`GIT_CONFIG_SYSTEM` pointed at
  empty files). A `[GitFact]` attribute skips them when git isn't installed.
- **Parser tests** also use captured outputs (strings) for edge cases that are hard to
  reproduce: unusual renames, unicode paths, CRLF, conflicts.
- **UI** is checked in the running app with `DEVHUB_DATA_DIR` set to a scratch folder and a
  scratch repository, never the user's real repos.

---

## 8. Risks / open questions

- **Big repositories:** status and log can be slow (Unity projects have many files). Mitigate
  with `--untracked-files=normal`, cancellation, virtualized lists, paging the log.
- **Line endings / encodings** in diffs (`core.autocrlf`, UTF-16 files): show "binary or
  unsupported encoding" rather than garbage.
- **Unity specifics:** `.meta` files, LFS-tracked assets; show them, don't special-case yet.
- **Git missing or too old:** a clear empty state with a link to install, not errors.
- **Hooks** (pre-commit) can take long or fail: stream their output into the Console and the
  commit dialog.
