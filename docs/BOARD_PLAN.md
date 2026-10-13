# Board — plan

A Jira-style planning board inside DevHub: epics, stories, tasks, bugs and sub-tasks, story points and time
tracking, a Kanban board with columns you shape yourself, a ranked backlog, sprints, and (with Git) links to
branches and commits. **No server**: a board is a set of plain JSON files. On its own, Board keeps them in a
folder; with `Board.Git` a repository's board can live on a branch of its own (`devhub/board`), versioned and
shared through git. Milestones and checkboxes live in DevHub's `docs/ROADMAP.md` ("B — Board"); this file is the
design they follow.

---

## 1. Decisions (confirmed 2026-10-13)

| Decision | Choice |
|---|---|
| Planning without git | **Board needs no git and no other plugin.** Someone who only wants to plan gets a full board stored in a folder. Git is an optional extra, added by `Board.Git`. |
| Where the data lives | Behind a **store** (`IBoardStore`, §3). Board ships the **folder store** (a board's files in a folder on disk). `Board.Git` adds the **branch store**: the files on a dedicated `devhub/board` branch written with git plumbing, never checked out, so the board is the same on every code branch and never mixes with code commits. For a git repository with `Board.Git` on, the branch store is the default. |
| Users | **One person for now.** Multi-user (merging two people's edits, renumbering colliding keys, `team.json`) is a later milestone (§10); the file format is chosen so it can be added without a migration. |
| First usable version | **`Board` + `Board.Scrum`** (epics, stories, points, columns, backlog, sprints), then `Board.Git`. |
| Estimates | **Story points and time tracking** (original estimate, remaining, work logged), like Jira. |
| Issue types | **Epic, story, task, bug, sub-task** to start; custom types with B2's editor. |
| Plugins | `DevHub.Board` (+ `.Core`, requires nothing); `DevHub.Board.Scrum` → Board; `DevHub.Board.Git` → Board + Git (branch store, branch / commit links); later `DevHub.Board.Reports` and `DevHub.Board.Timeline` → Board. Switch off what you don't use. |
| Project vs board | A board store holds **one project** (one key, e.g. `DEV`) and any number of **boards** (views onto its issues: own columns, filter, swimlanes). |

---

## 2. What it looks like

The board opens as an **editor tab** ("Board — DevHub"). Ways in: Find Action ("Open Board", "New Board"), the
Welcome page, a project's context menu in the Projects tool window (when Projects is on, through a hook), and the
Git repository tab's toolbar (with `Board.Git`). Recently opened boards are remembered.

**New Board** asks: name, project key (`DEV`), preset (Scrum, Kanban, Bug triage, empty), and where to keep it:
- **In DevHub** (default when there's no project): `%APPDATA%/DevHub/boards/<key>/`. Nothing else needed.
- **In a folder you choose**: e.g. a project's `.devhub/board/` (versioned with the code if it's in a repository),
  or a synced folder (OneDrive) for a personal board on two machines.
- **On the repository's `devhub/board` branch** (only with `Board.Git`, and only for a git repository).

A board can be moved between stores later ("Move board…"), so starting in a folder loses nothing.

```
┌─ Board — DevHub ─────────────────────────────────────────────────────────────────────────────┐
│ [Board ▾ Main board]  [Backlog]  [Sprints]     Quick filters: (Only mine) (Bugs) (Epic: Login ▾) │
│ Sprint 4 · ends Fri · 21 / 34 pts                                          [+ Create issue  C] │
│ ┌ To Do (3) ─────┐ ┌ In Progress (2) 3/4 ┐ ┌ Review (1) ───┐ ┌ Done (5) ──────┐ [+ column]      │
│ │ ┌────────────┐ │ │ ┌────────────┐      │ │ ┌───────────┐ │ │ ┌────────────┐ │                 │
│ │ │■ DEV-12    │ │ │ │▲ DEV-9     │      │ │ │■ DEV-7    │ │ │ │✓ DEV-3     │ │                 │
│ │ │Login form  │ │ │ │Save tokens │      │ │ │Avatar     │ │ │ │Settings    │ │                 │
│ │ │Login · 3 ●A│ │ │ │Login · 5 ●A│      │ │ │ 2       ●A│ │ │ │ 1          │ │                 │
│ │ └────────────┘ │ │ └────────────┘      │ │ └───────────┘ │ │ └────────────┘ │                 │
│ └────────────────┘ └─────────────────────┘ └───────────────┘ └────────────────┘                 │
└───────────────────────────────────────────────────────────────── issue side panel ▸ DEV-12 ───┘
  card: type icon, key, summary, epic, story points, assignee avatar (configurable per board)
```

- **Board**: drag cards between columns (only to statuses the workflow allows) and up / down to rank them.
  Column headers have a menu: rename, set WIP limit, statuses in this column, merge with…, delete.
- **Backlog**: the ranked list of everything not done, with the sprints above it (Scrum) and an epics panel on
  the left; drag issues into a sprint, see point totals per sprint and per epic.
- **Issue view**: a side panel on the board (click a card) or its own tab (double-click); every field editable
  in place, Markdown description, comments, sub-tasks, links, time tracking, history.

---

## 3. Stores

### The seam

```csharp
public interface IBoardStore
{
    string DisplayLocation { get; }                       // "C:\…\boards\DEV" or "DevHub · devhub/board"
    Task<BoardLoad> LoadAsync(CancellationToken ct);      // every file + a version token
    Task<StoreWrite> WriteAsync(BoardChange change, string expectedVersion, CancellationToken ct);
    event EventHandler? ChangedOutside;                   // something other than this window changed the files
}
// BoardChange = files to write / delete + a message ("DEV-12: status To Do → In Progress")
// StoreWrite  = Written(newVersion) | Conflict (the store moved: reload, re-apply)
```

The rest of Board (model, operations, views) only sees files and a version, never where they live. Stores come
from `IBoardStoreProvider`s registered in DI (Board registers the folder provider; `Board.Git` adds the branch
provider), so Board never knows git exists. (Unity analogy: like swapping a `PlayerPrefs` save for a file save
behind the same `ISaveSystem` interface.)

Every write is a **compare-and-swap**: it says which version it was based on, and the store refuses if the data
moved meanwhile (another DevHub window, a sync, a `git pull`). Board then reloads and re-applies the change, so
nothing is ever silently overwritten. Writes go through **one queue per board**, and text fields are saved when
they lose focus, not per keystroke.

### Folder store (Board)

- The files (§4) in a folder. Load = read the folder; the version token is a counter in `board.json`.
- **Write**: take a lock file (`.lock`, short-lived), check the counter, write each changed file to a temp name
  and rename it over the old one (a crash never leaves a half-written file), bump the counter, release.
- **Outside changes**: a `FileSystemWatcher` on the folder (ignoring our own writes) raises `ChangedOutside`.
- Inside a git repository, the folder is just files: you commit them with your code like anything else.

### Branch store (Board.Git)

`devhub/board` is an **orphan** branch: its history has nothing in common with your code and its tree holds only
board files. A git branch is a name pointing at a commit, and git's **plumbing** commands (the low-level ones
`git commit` is built from) can read and write files inside a commit without touching your folder, your staging
area or your current branch. If you ever check the branch out by accident you just see JSON files.

- **Read**: `git rev-parse refs/heads/devhub/board` (the version token), `git ls-tree -r -z`, then one
  `git cat-file --batch` process streaming every file: a few hundred issues in one round trip.
- **Write** (one commit per change):
  1. `git hash-object -w --stdin` per changed file → blob ids;
  2. a temporary index (`GIT_INDEX_FILE` under `.git/devhub/`, so your real staging area is never used):
     `git read-tree <version>`, `git update-index --add --cacheinfo 100644,<blob>,<path>` (or `--remove`),
     `git write-tree` → tree;
  3. `git commit-tree <tree> -p <version> -m "<message>"` → commit (author = the repository's `user.name`);
  4. `git update-ref refs/heads/devhub/board <new> <version>`: git's own compare-and-swap.
- **Outside changes**: the ref is checked when the tab is activated and after Git's `IRepositoryEvents`.
- Runs through Git's `GitRunner`: every command shows in the Git Console; the Git client rules apply.
- **Push board / Pull board**: it's an ordinary branch, so push backs it up and fetch brings it to another machine.
  A fast-forward pull just moves the branch; a diverged one (edited on two machines) says so and waits for the
  multi-user milestone (§10), losing nothing.
- Git's branch list shows `devhub/board` like any branch; `Board.Git` marks it ("Board data") via `IGitDecorator`.

---

## 4. Files

The same files in every store:

```
board.json              the project: key, next issue number, issue types, priorities, custom fields, store counter
workflows/<id>.json     statuses, their categories, allowed transitions, resolutions
boards/<id>.json        a board: columns → statuses, filter, swimlanes, quick filters, card layout, WIP limits
issues/<id>.json        one file per issue
history/<id>.jsonl      one per issue: its change log, one line per change (append-only)
sprints/<id>.json       one file per sprint (Board.Scrum)
versions/<id>.json      fix versions / releases (later: B10)
attachments/<issue id>/<file>                                 (B4; size-limited)
```

Every JSON file has `"schema": 1`, so a later format change can migrate old files on read.

### Ids and keys

- Every issue, sprint, board, workflow and status has a **stable id**: a short random string (`k7f3a9c2`) that
  never changes. File names and references between files use ids, never keys.
- The **key** (`DEV-12`) is what people see and type in branch names and commits. It's stored in the issue; the
  next number lives in `board.json`. With one user keys never collide; when multi-user comes, a collision after a
  merge renumbers one issue and keeps the old key as an alias, so old commit messages still link (§10).

### An issue

```json
{
  "schema": 1,
  "id": "k7f3a9c2",
  "key": "DEV-12",
  "type": "story",
  "summary": "Login form",
  "description": "As a user I want to sign in so that…\n\n- [ ] email + password",
  "status": "s-inprogress",
  "resolution": null,
  "priority": "medium",
  "parent": "e81bc044",
  "assignee": "ada",
  "reporter": "ada",
  "labels": ["auth"],
  "components": [],
  "fixVersions": [],
  "storyPoints": 3,
  "time": {
    "original": "PT8H",
    "remaining": "PT4H",
    "log": [ { "id": "w1", "who": "ada", "started": "2026-10-13T09:00:00Z", "spent": "PT4H", "note": "Layout" } ]
  },
  "due": null,
  "sprint": "p2c91d0e",
  "rank": "0|hzzzzz:",
  "links": [ { "type": "blocks", "target": "a0d4e7f1" } ],
  "custom": { "f-team": "Platform" },
  "comments": [ { "id": "c1", "who": "ada", "created": "2026-10-13T09:12:00Z", "body": "Started on this." } ],
  "created": "2026-10-12T15:40:00Z",
  "updated": "2026-10-13T09:12:00Z"
}
```

- **Epics and sub-tasks are just `parent`**: a story's parent is an epic, a sub-task's parent is a story. Which
  type may be whose parent is part of the issue type definition (`board.json`), so custom hierarchies work.
- **Links** are stored on one side only (the issue that "blocks"); the other side ("is blocked by") is computed.
- **Sprint** is a single field: an issue is in one sprint or in the backlog. A finished sprint keeps the list of
  issues it completed and the points committed / completed, for reports.
- **Time tracking**: durations as ISO 8601 (`PT4H`), shown and typed Jira-style (`1d 4h`, with a configurable
  hours-per-day). Logging work lowers "remaining" by the time spent unless you set it yourself. Sub-task time adds
  up on the parent.

### History

Each operation appends a line to the issue's `history/<id>.jsonl`:
`{"at":"…","who":"ada","field":"storyPoints","from":3,"to":5}`. The issue view's History tab reads it
(`Story points 3 → 5 · Ada · 2 hours ago`). Same mechanism in every store, so history doesn't depend on git; the
branch store additionally has the git log. Undo of the last change = an operation that writes the old values back.

### Ranking without rewriting everything

The backlog and each column are ordered by `rank`, a string compared alphabetically. Moving an issue between two
others gives it a rank *between* theirs (`"a"` and `"b"` → `"an"`), so a drag changes **one file**, not every
issue below it. (Jira does the same; it calls it LexoRank.) When ranks grow long, a "rebalance" rewrites them all
in one write; that's rare.

### People

`board.json` has a `people` list (`id`, display name, optional email for the avatar); for one user it's just you,
filled in from Settings on the first board. `Board.Git` adds the repository's git authors as suggestions. Picked
with the Sdk's `PeoplePicker`, shown with `Avatar` (GitHub avatars when GitHub is on).

---

## 5. Workflows and columns

- A **status** has a name and a category: **To Do**, **In Progress** or **Done**. Categories drive everything
  generic (what counts as finished for points and reports; Done ⇒ a resolution is set).
- A **workflow** lists statuses and the **transitions** between them ("any → any" by default). Each issue type
  uses one workflow (all the same unless you change it).
- A **board column** holds one or more statuses. Nothing is fixed:
  - **Add a column** on the board → it creates a status of the same name (category asked, In Progress by default)
    and adds it to the workflow.
  - **Rename / reorder (drag) / merge** columns; **delete** asks where its issues go. You can delete every default
    column and start from nothing.
  - Statuses on no column are listed as "Not on this board" (with a count), so issues never silently disappear.
- **Presets** when creating a board: Scrum (To Do, In Progress, Review, Done), Kanban (Backlog, Selected, In
  Progress, Done), Bug triage, Empty. A preset is only a starting point, written into the files once.

---

## 6. Code layout (DevHub-Plugins)

```
plugins/Board/DevHub.Board.Core/       no Avalonia, no git; tested
  Model/        Issue, IssueType, Status, Workflow, BoardDefinition, Column, Sprint, Rank, IssueKey, Duration, …
  Storage/      IBoardStore, IBoardStoreProvider, FolderBoardStore, BoardSnapshot, BoardJson (schema + migration)
  Operations/   CreateIssue, UpdateField, Transition, MoveCard, Rerank, LogWork, AddColumn, DeleteColumn, …
                (plain argument objects: each validates against the snapshot and returns the changed files,
                history lines and a message; also what M10's command tools will call)
  BoardSettings (ISettingsSection: recent boards, last opened board, card density, hours per day)
plugins/Board/DevHub.Board/            UI: BoardPlugin, BoardDocument (tab), board / backlog / issue views,
                                       New Board and Create Issue dialogs, actions, Themes/Board.axaml
plugins/BoardScrum/DevHub.Board.Scrum(.Core)/   sprints, sprint header, start / complete dialogs, capacity
plugins/BoardGit/DevHub.Board.Git(.Core)/       BranchBoardStore, branch / commit links, Git decorations
tests/DevHub.Board.Core.Tests/         model, operations, folder store (temp folders)
tests/DevHub.Board.Git.Core.Tests/     branch store with real git (TempGitRepo, [GitFact])
```

**Why operations are separate objects:** a ViewModel never edits an `Issue` and writes JSON itself. It builds an
operation (`new Transition(issueId, toStatus)`), the Core checks it (is the transition allowed? does the status
exist?) and returns what to write. Every rule is testable without a window, and undo, history, commit messages
and the future natural-language commands share one code path.

**Optional links without depending on each other:** Board opens from a Projects folder through a small hook
(Projects isn't required); `Board.Git` uses Git's hooks (`IGitDecorator`, `ICommitMessageSuggester`,
`IRepositoryEvents`) and requires Board + Git. Board itself requires nothing.

Building blocks already in the Sdk (from PM6): `ItemsDragDrop` (cards between columns, insertion line),
`MarkdownView` (descriptions, comments), `PeoplePicker` + `Avatar`, `LineChart` / `BarChart` (reports, sprint
burndown), `ListMoves`. **No Sdk change is expected for B1–B7**; if one turns up, it's released in DevHub first.

---

## 7. Milestones (the order we build in)

Each step ends with something that builds and is tested; UI steps end with a manual check list.

| Step | What | Done when |
|---|---|---|
| **B1** | **Core model + folder store**: model types, JSON format, ids / keys, ranks, durations, `IBoardStore` + the folder store (load, compare-and-swap write, outside changes), operations for create / edit / comment / link / log work, history. | Tests create a board in a temp folder, make changes, reload them, refuse a stale write, and read the history. |
| **B2** | **Workflows**: statuses + categories, transitions, resolutions, presets; operations for add / rename / delete status. | Tests for allowed / refused transitions and deleting a status in use. |
| **B3** | **Board tab**: New Board / open / recent boards, columns from the board definition, cards, drag between columns and to rank, the column menu (add, rename, reorder, merge, delete, WIP limit), quick filters (mine, type, epic, label). | Create a board, reshape every column, move cards; restart shows the same board. |
| **B4** | **Issue view + Create Issue**: side panel and tab, every field editable, Markdown description, comments, sub-tasks, links, time tracking (estimate, remaining, log work), history tab. | Create an epic, a story under it and a sub-task; edit every field; log work; history lists each change. |
| **B5** | **Backlog**: ranked list, epics panel (filter by epic, epic progress), point totals, multi-select + bulk edit. | Reorder the backlog, filter by epic, bulk-set points. |
| **B7** | **`Board.Scrum`**: sprints (create, plan by dragging from the backlog, start with goal + dates, complete with leftovers to backlog or next sprint), the sprint board, capacity, committed vs completed points. | **First usable version**: a full sprint can be planned, run and closed. |
| **B8** | **`Board.Git`**: the branch store (new board on `devhub/board`, Move board…, Push / Pull board); "Create branch" from an issue (`feature/DEV-12-login-form`); keys in branch names and commits shown on the issue (Development section) and as marks in Git's commit list and branches; the commit box suggests `DEV-12: ` on that branch; pull requests through GitHub when it's on; smart commits (`DEV-12 #done`) offered, never applied silently. | A board on the branch survives switching code branches untouched; from an issue, create a branch, commit, and see the commit on the issue. |
| later | **B6** search language (JQL-like) + saved filters · **B9** reports (burndown, velocity, CFD) · **B10** timeline + releases · **B11** import / export (Jira CSV, GitHub Issues) · **B12** M10 command tools · **multi-user** (§10). | |

B6 (search) moves after the first usable version: B3's quick filters cover the everyday cases.

---

## 8. Rules

- **Board needs nothing but itself.** Git, Projects and GitHub are optional; every link to them has a fallback.
- **Every write is a compare-and-swap** against the version it was based on; a moved store means reload, never
  overwrite. Folder writes are temp-file + rename; one write queue per board.
- **The branch store never touches the working tree, the index or HEAD**: plumbing on `refs/heads/devhub/board`
  with a temporary index under `.git/devhub/`, through `GitRunner`. A test checks it for every operation.
- Files are written with sorted keys and stable formatting (same data ⇒ same bytes), so diffs and merges stay small.
- Unknown fields in a file are **kept** when it's rewritten (a newer DevHub's data survives an older one's edit).
- Rules live in `.Core` operations, tested; ViewModels build operations and show results.

---

## 9. Board vs Jira: deliberately left out (for now)

Permissions and roles, notifications / email, automation rules, multiple projects in one store, issue security
levels. They make sense on a server with many people; a local board doesn't need them.

---

## 10. Later: multi-user

Not built now, but the format allows it:
- **Merging** two versions of a board (two machines, two people): a file changed on one side only is taken as is;
  an issue changed on both is merged **field by field** (different fields: both kept; same field: the later change
  wins and the issue shows "changed on both sides"); comments, work logs and history lines are sets (both kept).
- **Key collisions** (`DEV-13` created on both sides): the later one is renumbered, its old key kept as an alias.
- **`team.json`**: members, display names, who can be assigned.
- `board.json`'s next number becomes a hint; the real next number is the highest key + 1 after a merge.
