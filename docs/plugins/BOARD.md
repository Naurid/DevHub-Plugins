# Board and its plugins

Jira-style planning without a server: epics, stories, tasks, bugs and sub-tasks, story points and time tracking,
boards with columns you shape yourself, a ranked backlog, search, sprints, reports, a roadmap and a risk register. **A
board is a folder of plain JSON files**; with Board Git it can live on a repository branch of its own. Design and
history: [`docs/BOARD_PLAN.md`](../BOARD_PLAN.md).

| Plugin | Id | Requires | Adds |
|---|---|---|---|
| Board | `devhub.board` | – | boards, issues, backlog, search, import / export, the Boards tool window |
| Board Scrum | `devhub.board.scrum` | Board | sprint planning in the backlog, Retrospectives |
| Board Git | `devhub.board.git` | Board, Git | the `devhub/board` branch store, branches and commits per issue, smart commits |
| Board Reports | `devhub.board.reports` | Board | burndown, velocity, flow, forecasts, KPIs, dashboards |
| Board Timeline | `devhub.board.timeline` | Board | roadmap of epics, releases |
| Board Risks | `devhub.board.risks` | Board | risk register and probability × impact matrix |

Code: `plugins/Board*/`; the logic of each in its `.Core` (`Board.Scrum` has none: sprint data and rules are in
`Board.Core`, so search and reports understand sprints without it), tested in `tests/DevHub.Board*.Core.Tests`.
`tools/BoardDemo` writes a demo board that uses every feature.

---

## Board (`devhub.board`)

### Using it

- **New Board…** (Board menu, Welcome): name, project key (`DEV`), preset (Scrum: To Do, In Progress, In Review, Done;
  Kanban: Backlog, Selected for Development, In Progress, Done; Bug triage; Empty) and where to keep it: in DevHub
  (`%APPDATA%\DevHub\boards\<key>`), in a folder you choose, or (with Board Git) on the repository's branch. **Board
  of This Project** opens or creates the board of the tab in front's folder (`<folder>\.devhub\board`, or its branch).
  **Open Board…** opens a board folder; recent boards are remembered. **Copy Board To…** copies one to another store.
- **The board tab**: a board selector (several boards per project, each with its own columns, filter, swimlanes,
  quick filters and card layout), its pages (Board, Backlog, Search; plugins add more), a search box, Create.
  - **Board**: drag cards between columns (only to statuses the workflow allows) and up / down to rank them. Columns
    are yours: add (creates a status), rename, reorder, merge, set WIP limits, delete (asks where the issues go);
    statuses on no column are listed so nothing disappears. Swimlanes by assignee, epic, priority or type.
  - **Backlog**: the ranked list with an epics panel, multi-selection and bulk edit, point totals.
  - **Search**: the query language below, saved filters, results as a list or a board.
  - **Diagrams** (with the Diagrams plugin): the project's diagrams.
- **Issues**: a side panel (click a card) or a tab of their own (double-click). Every field editable in place:
  summary, Markdown description, type, status, priority, assignee, reporter, labels, components, fix versions, story
  points, time tracking (estimate, remaining, work log), start and due dates, custom fields, acceptance criteria and
  the Definition of Done; comments, sub-tasks, links (blocks, relates, duplicates, clones), attachments, history.
  Sections and actions from other plugins (Board Git's Development, Create Branch).
- **Project Settings** (Board menu): issue types (custom ones too), statuses and workflows (transitions, any → any by
  default), priorities, resolutions, link types, custom fields, components, people and roles, Definition of Done
  (Off / Ask / Strict when moving to Done), hours per day.
- **Import / export**: Jira CSV (`docs/examples/jira-sample.csv` to try), CSV / JSON export, GitHub Issues import
  (with GitHub).
- **Undo Last Board Change** undoes your last change.

### Search language

JQL-like, case-insensitive; names or ids as values; errors point at their position.

```
type = bug AND statusCategory != Done ORDER BY priority DESC, created
assignee = currentUser() AND sprint in openSprints()
labels in (devhub, plugins) AND updated >= -7d
summary ~ "login" OR text ~ crash
```

- Operators: `= != > >= < <= ~ !~`, `[NOT] IN (…)`, `is empty`, `AND`, `OR`, `NOT`, parentheses, `ORDER BY field
  [ASC|DESC], …`.
- Fields: key, project, type, status, statusCategory, priority, resolution, assignee, reporter, labels, components,
  fixVersion, sprint, parent, epic, summary, description, comment, text, points, originalEstimate,
  remainingEstimate, timeSpent, created, updated, resolved, due, rank (ORDER BY only), and custom fields by name.
- Functions: currentUser(), openSprints(), closedSprints(), futureSprints(), now(), startOfDay(), endOfDay(),
  startOfWeek(), endOfWeek(), startOfMonth(), endOfMonth() (each with an optional offset: `startOfDay(-1)`).
- Dates: `2026-10-31`, or relative to now: `-1d`, `-2w`, `+3h`, `-30m`.

### Actions

| Id | Text | Shortcut |
|---|---|---|
| `Board.New` | New Board… (Welcome) | |
| `Board.Open` | Open Board… (Welcome) | |
| `Board.ForProject` | Board of This Project | |
| `Board.CreateIssue` | Create Issue… | Ctrl+Alt+I |
| `Board.Backlog` | Show Backlog | |
| `Board.Search` | Search Issues | |
| `Board.GoToIssue` | Go to Issue… | |
| `Board.MyOpenIssues` | My Open Issues | |
| `Board.Undo` | Undo Last Board Change | |
| `Board.ProjectSettings` | Board Settings | |

All in the Board menu and Find Action. Tabs: `Board:<location>` (a board), one per issue opened on its own, one per
board's Project Settings. Tool window: **Boards** (left stripe), the recent boards.

### Settings (`devhub.board`, Settings → Board)

| Field | Meaning |
|---|---|
| `UserName`, `UserEmail` | Who you are on boards (empty name = the Windows user name). The email matches you to git authors and avatars. "Mine" filters and `currentUser()` use them. |
| `CompactCards` | Smaller cards. |
| `RecentBoards`, `LastBoardView` | Remembered boards and the board view shown last per board. |

### The files

```
board.json              the project: key, next number, issue types, statuses, priorities, resolutions, link types,
                        custom fields, components, people, Definition of Done, settings
workflows/<id>.json     statuses (in order) and transitions
boards/<id>.json        a board: columns → statuses, filter, swimlanes, quick filters, card fields, WIP limits
issues/<id>.json        one file per issue (stable id; the key DEV-12 can change)
history/<id>.jsonl      one per issue, append-only: one line per change
sprints/<id>.json       sprints            versions/<id>.json  fix versions
filters/<id>.json       saved filters      attachments/<issue id>/<file>
retros/<sprint>.json    retrospectives (Board Scrum)      risks/<id>.json  risks (Board Risks)
reports/baselines.json  forecast baselines (Board Reports)
```

Every file has `"schema": 1`. Every write is a compare-and-swap on the store's version: when the files changed
meanwhile (another window, a sync, a pull), the change is re-applied on the new version, never written over it. The
folder store writes each file to a temp name and renames it, under a short-lived `.lock`. The diagram
`docs/diagrams/Board data model` draws these files as classes.

### Extension points (for plugins that require Board)

Defined in `DevHub.Board` (`Extensibility/BoardExtensions.cs`) and `DevHub.Board.Core` (`Storage/IBoardStore.cs`);
diagram: `docs/diagrams/Board extension points`.

| Interface | A plugin adds | Implemented by |
|---|---|---|
| `IBoardPageProvider` (`Id`, `Title`, `Order`, `Replaces`, `AppliesTo(definition)`, `Create(host)`) | a page of the board tab; `Replaces` swaps out another page | Board (board 100, backlog 200, diagrams 800, search 900), Scrum (planning replaces the backlog on Scrum boards, retrospectives 650), Timeline (600, releases 650), Risks (680), Reports (700) |
| `BoardPageViewModel` (base) | the page: `Host`, `Context`, `OnBoardChanged`, `OnSearch`, `OnShown`, `Dispose` | each page |
| `BoardPageHost` | what a page gets: `Context`, `Definition`, `SelectIssue(s)`, `OpenIssue`, `CreateIssue(defaults)`, `ShowPage` | Board |
| `IIssueSectionProvider` (`Order`, `Create(context, issueId)`) | a section in the issue view (sub-tasks 100, links 200, attachments 300) | Board (Diagrams), Board Git (Development) |
| `IIssueActionProvider` (`GetActions(context, issue)`) | `IssueAction`s in the issue's header menu and card menu | Board Git (Create Branch) |
| `IBoardStoreProvider` (`Kind`, `DisplayName`, `CanUseAsync`, `Open`) → `IBoardStore` (`LoadAsync`, `WriteAsync(change, expectedVersion)`, `ChangedOutside`) | a place to keep boards | Board (`folder`), Board Git (`git-branch`) |

Board operations are plain argument records (`CreateIssue`, `TransitionIssue`, `EditIssue`, `PlanIssue`, …) applied
through a `BoardSession`; they're what a future command interpreter (roadmap M10) maps to.

**Hooks**: offers `IIssueLinks` (diagram shapes linked to issues, Create Story); uses `IProjectDiagrams` (Diagrams page
and issue section) and `IGitHubApi` (GitHub Issues import), both optional.

---

## Board Scrum (`devhub.board.scrum`)

- **Backlog with sprints** (replaces the plain backlog on Scrum boards): create sprints, drag issues into them, see
  points and capacity per sprint; start one with a goal and dates (what was committed is recorded); complete it and
  move what's left to the next sprint or the backlog.
- **Capacity**: each person's available days and a focus factor; a capacity suggested from the last sprints' points
  per focused day (none for a new team).
- **Retrospectives** page: went well, to improve, problems, actions with owners and issues, follow-up.
- Actions: `Board.CreateSprint` (Create Sprint), `Board.SprintPlanning` (Sprint Planning), Board menu.

## Board Git (`devhub.board.git`)

Needs git installed (registers nothing without it).

- **The branch store**: a board on the repository's orphan `devhub/board` branch, written with git plumbing (a
  temporary index under `.git/devhub/`, `commit-tree`, `update-ref` as compare-and-swap) and never checked out: the
  same board on every code branch, never mixed with code commits, one commit per change. **Push Board** / **Pull
  Board** (`Board.PushBoard`, `Board.PullBoard`) back it up and bring it to another machine; a pull that diverged says
  so and loses nothing. The branch is marked "Board data" in Git's branch list.
- **Development** section in an issue: the branches whose name holds its key, the commits whose message does, and
  (with GitHub) pull requests.
- **Create Branch** from an issue: `feature/DEV-12-short-summary` (`bugfix/` for bugs).
- **Issue keys** next to commits and branches in Git (`IGitDecorator`); the commit box suggests the current branch's
  key ("DEV-12: ", `ICommitMessageSuggester`); a status-bar widget shows the current branch's issue.
- **Smart commits**: `DEV-12 #done #comment fixed the layout #time 2h` in a commit message (from DevHub or made outside
  it) is shown for confirmation before it applies, never silently. A status command names a status (spaces as
  hyphens: `#in-progress`) or a category (`#done`).
- Settings (`devhub.board.git`): `Repositories` (which repository a board's issues are developed in, when it isn't
  obvious), `SmartCommits` (default on), `SuggestCommitMessage` (default on).
- Uses Git's `GitRunner` (every command shows in the Git Console), `IRepositoryEvents`, and optionally `IGitHubApi`.

## Board Reports (`devhub.board.reports`)

A **Reports** page, computed from the board and each issue's history:

- Sprints: burndown, burnup, sprint report, velocity, commitment reliability, sprint plans, capacity and load.
- Flow: cumulative flow, control chart, lead time, throughput, WIP, defects.
- Scope and plans: epic progress, scope change, release burnup and forecast (pessimistic / neutral / optimistic
  velocity and a planned ramp-up, delivery dates against a target), baselines to compare with.
- A **dashboard** of gadgets per board (filter results, a pie by field or filter, assigned to me).
- Every chart in a chosen chart type, exported as PNG or copied, its table as CSV; **Open in Chart Maker** with the
  Charts plugin (`IChartMaker`).
- Settings (`devhub.board.reports`): `Dashboards`, `ChartKinds` (chart type per report), `CurvedLines`, `Forecasts`.

## Board Timeline (`devhub.board.timeline`)

- **Timeline** page: a roadmap of epics from start to due date (dates guessed from their issues when they have
  none), progress, late epics and blockers.
- **Releases** page: fix versions with their dates and progress, and release notes from their issues.

## Board Risks (`devhub.board.risks`)

- **Risks** page: a risk register (title, category, probability and impact 1–5, owner, mitigation and contingency
  plans), the probability × impact matrix as a traffic-colored heatmap, export.
