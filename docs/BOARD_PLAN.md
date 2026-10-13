# Board — plan

A Jira-style planning board inside DevHub: epics, stories, tasks, bugs and sub-tasks, story points, a Kanban board
with columns you shape yourself, a ranked backlog, sprints, and links to branches and commits. **The data lives in
the repository** (plain JSON files on a branch of their own), so there is no server, every change is versioned, and
the board travels with the repository. Milestones and checkboxes live in DevHub's `docs/ROADMAP.md`
("B — Board"); this file is the design they follow.

---

## 1. Decisions

| Decision | Choice |
|---|---|
| Where the data lives | **A dedicated `devhub/board` branch**, written with git plumbing (§3), never checked out. The board is the same whichever code branch you're on, and board changes never mix with code commits. (confirmed 2026-10-13) |
| Users | **One person for now.** Multi-user (merging two people's edits, renumbering colliding keys, `team.json`) is a later milestone (§9); the file format is chosen so it can be added without a migration. (confirmed 2026-10-13) |
| First usable version | **`Board` + `Board.Scrum`** (epics, stories, points, columns, backlog, sprints), then the branch / commit links. (confirmed 2026-10-13) |
| Git access | **Board requires the Git plugin** (a hard requirement, like GitHub → Git). It runs git through Git's `GitRunner`, so every board write shows up in the Git Console and follows the Git client rules. Git is bundled, and a board stored in git can't work without it anyway. *(proposed)* |
| Branch / commit links | **Part of `Board` itself** instead of a separate `Board.Git` plugin: once Board requires Git, a separate plugin would only add a checkbox. They come as their own milestone (B8) after sprints. *(proposed)* |
| Plugins | `DevHub.Board` (+ `.Core`) → Git; `DevHub.Board.Scrum` → Board; later `DevHub.Board.Reports` and `DevHub.Board.Timeline` → Board. Kanban-only users switch Scrum off. |
| One board store per repository | A repository has **one project** (one key, e.g. `DEV`) and any number of **boards** (views onto its issues). Several projects per repository: not planned. |

---

## 2. What it looks like

The board opens as an **editor tab per repository** ("Board — DevHub"), from Find Action ("Open Board"), the
Git repository tab's toolbar, or the Projects tool window's context menu. A repository without a board offers
"Create board" (asks the project key and a preset: Scrum, Kanban, or empty).

```
┌─ Board — DevHub ─────────────────────────────────────────────────────────────────────────────┐
│ [Board ▾ Main board]  [Backlog]  [Sprints]     Quick filters: (Only mine) (Bugs) (Epic: Login ▾) │
│ Sprint 4 · ends Fri · 21 / 34 pts                                          [+ Create issue  C] │
│ ┌ To Do (3) ─────┐ ┌ In Progress (2) 3/4 ┐ ┌ Review (1) ───┐ ┌ Done (5) ──────┐ [+ column]      │
│ │ ┌────────────┐ │ │ ┌────────────┐      │ │ ┌───────────┐ │ │ ┌────────────┐ │                 │
│ │ │■ DEV-12    │ │ │ │▲ DEV-9     │      │ │ │■ DEV-7    │ │ │ │✓ DEV-3     │ │                 │
│ │ │Login form  │ │ │ │Save tokens │      │ │ │Avatar     │ │ │ │Settings    │ │                 │
│ │ │Login · 3 ●G│ │ │ │Login · 5 ●G│      │ │ │ 2       ●G│ │ │ │ 1          │ │                 │
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
  in place, Markdown description, comments, sub-tasks, links, history.

---

## 3. Storage: the `devhub/board` branch

### What a "branch nobody checks out" means

A git branch is just a name pointing at a commit, and a commit points at a tree of files. Normally you check a
branch out and git copies its files into your folder. Git's **plumbing** commands (the low-level ones that
`git commit` is built from) can also read and write files *inside* a commit directly, without touching your
folder, your staging area or your current branch. That's how Board works: your code stays on `main` or
`feature/x`, and the board's files live in their own line of commits next to it.
(Unity analogy: like a ScriptableObject asset every scene shares, instead of an object living in one scene.)

`devhub/board` is an **orphan** branch: its history has nothing in common with your code, and its tree holds only
board files. If you ever check it out by accident you just see those JSON files; nothing breaks.

### Reading

- On open: `git rev-parse refs/heads/devhub/board` (missing → no board yet), then
  `git ls-tree -r -z <commit>` for the file list and **one** `git cat-file --batch` process that streams every
  file's contents. A few hundred issues load in one round trip.
- The whole board is kept in memory (`BoardSnapshot`); views read the snapshot, never git.
- **Outside changes** (a push from another machine was pulled, a `git reset`): the ref is checked when the tab is
  activated and after Git's `IRepositoryEvents` (fetch / pull); a changed commit id reloads the snapshot.

### Writing

Each user change (move a card, edit a field, add a comment) becomes **one commit** on `devhub/board`:

1. `git hash-object -w --stdin` for each changed file → blob ids.
2. A temporary index (`GIT_INDEX_FILE` pointing into `.git/devhub/`, so your real staging area is never used):
   `git read-tree <current commit>`, then `git update-index --add --cacheinfo 100644,<blob>,<path>` (or
   `--remove`) per file, then `git write-tree` → the new tree.
3. `git commit-tree <tree> -p <current commit> -m "<message>"` → the new commit. Messages say what happened:
   `DEV-12: status To Do → In Progress`, `DEV-14: created "Login form"`.
4. `git update-ref refs/heads/devhub/board <new> <current>`: the last argument makes it a **compare-and-swap**: if
   the branch moved meanwhile (another DevHub window, a pull), git refuses, Board reloads and re-applies the change.

Writes go through **one queue per repository**, so two quick edits can't race. Rapid edits of the same field
(typing a description) are saved when the field loses focus, not per keystroke.

The author is the repository's `user.name` / `user.email`, like a normal commit.

### History for free

"Who changed what, when" is **not** stored separately: it's the branch's history. The issue view's History tab
runs `git log --format=… -- issues/<id>.json` and diffs successive versions field by field
(`DEV-12: Story points 3 → 5, Ada, 2 hours ago`). Undo for the last change = write the previous version back.

### Sharing and backup (single user)

`devhub/board` is an ordinary branch, so `git push origin devhub/board` backs it up and a fetch on another
machine brings it over. Board offers **Push board** / **Pull board** actions: a pull that's a fast-forward just
moves the branch; a pull that has diverged (edited on two machines) says so and waits for the multi-user
milestone (§9), losing nothing. The Git plugin shows `devhub/board` in its branch list like any branch; Board
marks it there through `IGitDecorator` ("Board data").

---

## 4. Files on the branch

```
board.json              the project: key, next issue number, issue types, priorities, custom fields, settings
workflows/<id>.json     statuses, their categories, allowed transitions, resolutions
boards/<id>.json        a board: columns → statuses, filter, swimlanes, quick filters, card layout, WIP limits
issues/<id>.json        one file per issue
sprints/<id>.json       one file per sprint (Board.Scrum)
versions/<id>.json      fix versions / releases (later: B10)
attachments/<issue id>/<file>                                 (B4; binary blobs, size-limited)
```

Every file has `"schema": 1`, so a later format change can migrate old files on read.

### Ids and keys

- Every issue, sprint, board, workflow and status has a **stable id**: a short random string (`k7f3a9c2`) that
  never changes. File names and references between files use ids, never keys.
- The **key** (`DEV-12`) is what people see and type in branch names and commits. It's stored in the issue and the
  next number lives in `board.json`. With one user keys never collide; when multi-user comes, a collision after a
  merge renumbers one issue and keeps the old key as an alias, so old commit messages still link (§9).

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
  "assignee": "Ada Lovelace <ada@example.com>",
  "reporter": "Ada Lovelace <ada@example.com>",
  "labels": ["auth"],
  "components": [],
  "fixVersions": [],
  "storyPoints": 3,
  "estimate": { "original": "1d", "remaining": "4h" },
  "due": null,
  "sprint": "p2c91d0e",
  "rank": "0|hzzzzz:",
  "links": [ { "type": "blocks", "target": "a0d4e7f1" } ],
  "custom": { "f-team": "Platform" },
  "comments": [ { "id": "c1", "author": "Ada Lovelace <ada@example.com>", "created": "2026-10-13T09:12:00Z", "body": "Started on this." } ],
  "created": "2026-10-12T15:40:00Z",
  "updated": "2026-10-13T09:12:00Z"
}
```

- **Epics and sub-tasks are just `parent`**: a story's parent is an epic, a sub-task's parent is a story. Which
  type may be whose parent is part of the issue type definition (`board.json`), so custom hierarchies work.
- **Links** are stored on one side only (the issue that "blocks"); the other side ("is blocked by") is computed.
- **Sprint** is a single field: an issue is in one sprint or in the backlog. A finished sprint keeps a list of the
  issues it completed, for reports.

### Ranking without rewriting everything

The backlog and each column are ordered by `rank`, a string compared alphabetically. Moving an issue between two
others gives it a rank *between* theirs (`"a"` and `"b"` → `"an"`), so a drag changes **one file**, not every
issue below it. (Jira does the same, it calls it LexoRank.) When ranks grow long, a "rebalance" rewrites them
all in one commit; that's rare.

### People

For one user: the repository's git authors (`git log --format=%an <%ae>` on the code branches) plus whoever is in
the board's files, shown with the Sdk's `PeoplePicker` and `Avatar` (GitHub avatars when GitHub is on).
`team.json` (display names, who's active) comes with multi-user.

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
plugins/Board/DevHub.Board.Core/       no Avalonia; tested
  Model/        Issue, IssueType, Status, Workflow, BoardDefinition, Column, Sprint, Rank, IssueKey, …
  Storage/      BoardBranchStore (read / write through GitRunner, §3), BoardSnapshot, BoardJson (schema + migration)
  Operations/   CreateIssue, UpdateField, Transition, MoveCard, Rerank, AddColumn, DeleteColumn, … (plain argument
                objects: each validates against the snapshot and produces the changed files + a commit message;
                this is also what M10's command tools will call)
  History/      field-level diff of two issue versions
  BoardSettings (ISettingsSection: last opened board per repository, card density)
plugins/Board/DevHub.Board/            UI: BoardPlugin, BoardDocument (tab), board / backlog / issue views,
                                       Create Issue dialog, actions, Themes/Board.axaml (card + column tokens)
plugins/BoardScrum/DevHub.Board.Scrum(.Core)/   sprints, sprint header, start / complete dialogs, capacity
tests/DevHub.Board.Core.Tests/         model and operations (pure), store tests with real git (TempGitRepo, [GitFact])
```

**Why operations are separate objects:** a ViewModel never edits an `Issue` and writes JSON itself. It builds an
operation (`new Transition(issueId, toStatus)`), the Core checks it (is the transition allowed? does the status
exist?) and returns what to write. That keeps every rule testable without a window, and gives undo, commit
messages and the future natural-language commands one code path.

Building blocks already in the Sdk (from PM6): `ItemsDragDrop` (cards between columns, insertion line),
`MarkdownView` (descriptions, comments), `PeoplePicker` + `Avatar`, `LineChart` / `BarChart` (reports, sprint
burndown), `ListMoves`. **No Sdk change is expected for B1–B7**; if one turns up, it's released in DevHub first.

---

## 7. Milestones (the order we build in)

Each step ends with something that builds and is tested; UI steps end with a manual check list.

| Step | What | Done when |
|---|---|---|
| **B1** | **Core model + store**: model types, JSON format, ids / keys, ranks, the `devhub/board` store (read, write, compare-and-swap, reload), operations for create / edit / comment / link, field history. | Tests create a board in a temp repo, make changes, reload them, and see that the working tree, index and HEAD were never touched. |
| **B2** | **Workflows**: statuses + categories, transitions, resolutions, presets; operations for add / rename / delete status. | Tests for allowed / refused transitions and deleting a status in use. |
| **B3** | **Board tab**: open / create a board, columns from the board definition, cards, drag between columns and to rank, the column menu (add, rename, reorder, merge, delete, WIP limit), quick filters (fixed ones: mine, type, epic, label). | You can create a board, reshape every column, and move cards; restart shows the same board. |
| **B4** | **Issue view + Create Issue**: side panel and tab, every field editable, Markdown description, comments, sub-tasks, links, history tab. | Create an epic, a story under it and a sub-task; edit every field; history lists each change. |
| **B5** | **Backlog**: ranked list, epics panel (filter by epic, epic progress), point totals, multi-select + bulk edit. | Reorder the backlog, filter by epic, bulk-set points. |
| **B7** | **`Board.Scrum`**: sprints (create, plan by dragging from the backlog, start with goal + dates, complete with leftovers to backlog or next sprint), the sprint board, capacity and committed vs completed points. | **First usable version**: a full sprint can be planned, run and closed. |
| **B8** | **Branches and commits**: "Create branch" from an issue (`feature/DEV-12-login-form`, through Git); keys found in branch names and commit messages, shown on the issue (Development section) and as marks in Git's commit list and branches (`IGitDecorator`); the commit box suggests `DEV-12: ` on that branch (`ICommitMessageSuggester`); pull requests through GitHub when it's on; smart commits (`DEV-12 #done`) offered, never applied silently. | From an issue, create a branch, commit, and see the commit on the issue. |
| later | **B6** search language (JQL-like) + saved filters · **B9** reports (burndown, velocity, CFD) · **B10** timeline + releases · **B11** import / export (Jira CSV, GitHub Issues) · **B12** M10 command tools · **multi-user** (§9). | |

B6 (search) moves after the first usable version: B3's quick filters cover the everyday cases.

---

## 8. Rules

- **Board never touches the working tree, the index or HEAD.** All git access is plumbing on `refs/heads/devhub/board`
  with a temporary index under `.git/devhub/`. A test checks it for every operation.
- Git only through `GitRunner` (the Git client rules apply: `ArgumentList`, machine-readable output, logged).
- **Every write is a compare-and-swap** (`update-ref <new> <old>`); a moved branch means reload, never overwrite.
- **One commit per user change**, with a message naming the issue and what changed.
- Files are written with sorted keys and stable formatting (same data ⇒ same bytes), so diffs and merges stay small.
- Unknown fields in a file are **kept** when it's rewritten (a newer DevHub's data survives an older one's edit).
- Rules live in `.Core` operations, tested; ViewModels build operations and show results.

---

## 9. Later: multi-user

Not built now, but the format allows it:
- **Merging** two lines of `devhub/board` file by file: a file changed on one side only is taken as is; an issue
  changed on both is merged **field by field** (different fields: both kept; same field: the later change wins
  and the issue shows "changed on both sides"); comments are a set (both kept).
- **Key collisions** (`DEV-13` created on both machines): the later one is renumbered, its old key kept as an alias.
- **`team.json`**: members, display names, who can be assigned.
- `board.json`'s next number becomes a hint; the real next number is the highest key + 1 after a merge.

---

## 10. Open questions (answer before or during B1)

1. **Plugin split**: Board requires Git and includes the branch / commit links (no separate `Board.Git`). OK?
2. **Estimates**: story points *and* time estimates (original / remaining, Jira-style), or story points only for
   now? (The format has room for both; the UI can leave time out.)
3. **Issue types**: start with epic, story, task, bug, sub-task (custom types come with B2's editor). OK?
