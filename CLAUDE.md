# DevHub-Plugins — Project Guide

Plugins for **DevHub** (a developer workbench: a bare shell that plugins fill), offered in its marketplace.
DevHub's own plugins live here too: **Projects, Git, GitHub, Templates** (moved from DevHub with their
history in PM4). DevHub bundles those four in its installer (from this repository's marketplace index), and
they update through the marketplace like any other plugin. New plugins (Board, Diagrams, CI) are built here.

The shell, the Sdk and the plugin system live in **Naurid/DevHub** (on this machine: `C:\P\DevHub\DevHub`).
Its docs are the reference for anything a plugin touches: `docs/PLUGINS.md` (contract, extension points,
linking, marketplace, building blocks), `docs/UI_GUIDE.md` (tokens, components, recipes, Avalonia 12 gotchas),
`docs/ACTIONS_AND_FUZZY_MATCHING.md`, and the roadmap `docs/ROADMAP.md` (milestones for both repositories).

## Stack
- .NET 10 (`net10.0`), Avalonia **12.x**, CommunityToolkit.Mvvm 8.4, Microsoft.Extensions.DependencyInjection,
  System.Text.Json, xUnit. Most Avalonia examples online are 11.x: check docs.avaloniaui.net before guessing.
- The Sdk comes from **nuget.org** (`DevHub.Sdk`, `DevHub.Sdk.Core`; `Directory.Build.props`, newest release by
  default). Unreleased Sdk changes: `pwsh build/Pack-Sdk.ps1` in DevHub → `%LocalAppData%\DevHub-local-feed`, then
  build here with `-p:DevHubSdkVersion=<the printed version>`.
- Avalonia, CommunityToolkit.Mvvm and DI are build-only (DevHub provides them at run time); keep their versions
  equal to DevHub's.

## Layout
```
plugins/<Name>/                  one plugin; grouped ones have a UI project and a .Core:
  Git/DevHub.Git/ + DevHub.Git.Core/            the Git client (repository tabs, diff, blame, Git Console)
  GitHub/DevHub.GitHub/ + DevHub.GitHub.Core/   GitHub account, Publish, IGitHubApi for other plugins (requires Git)
  Projects/DevHub.Projects/ + .Core/            Projects tool window, Open With
  Templates/DevHub.Templates/ + .Core/          New Project, Add Files, template editor and import; the engine
  Board/DevHub.Board/ + DevHub.Board.Core/       boards (no server, no git needed): issues, workflows, board / backlog / search pages,
                                                issue view, settings, import / export; extension points for the Board.* plugins
  BoardScrum/DevHub.Board.Scrum/                sprint planning, Start / Complete Sprint (requires Board; sprint rules are in Board.Core)
  BoardGit/DevHub.Board.Git/ + .Core/            devhub/board branch store, branches / commits of issues, smart commits (requires Board + Git)
  BoardReports/DevHub.Board.Reports/ + .Core/    burndown, velocity, flow, control chart, dashboard (requires Board)
  BoardTimeline/DevHub.Board.Timeline/ + .Core/  roadmap of epics, releases and release notes (requires Board)
  BoardRisks/DevHub.Board.Risks/ + .Core/        risk register, probability × impact matrix, mitigation (requires Board)
  Charts/DevHub.Charts/ + .Core/                 the chart maker: your own tables in every chart type, export (needs nothing)
  Diagrams/DevHub.Diagrams/ + .Core/            the diagram editor: canvas, tabs, export; the IDiagramKind extension point (needs nothing)
  DiagramsUml/DevHub.Diagrams.Uml/ + .Core/      UML: use case, activity, class, object, sequence, state, component, deployment, package
  DiagramsDatabase/DevHub.Diagrams.Database/ + .Core/  Merise MCD → MLD, crow's-foot ERD, SQL export / import, data dictionary
  DiagramsBusiness/DevHub.Diagrams.Business/ + .Core/  flowcharts, BPMN, mind maps
  MoodBoard/DevHub.MoodBoard/ + .Core/           mood boards: pictures, swatches, links, pen (the diagram kinds require Diagrams)
  Ci/DevHub.Ci/ + .Core/                         GitHub Actions runs of the open repository tabs, failed logs, re-runs (requires GitHub)
  (plugin.json sits next to the UI project: id, name, description, category, apiVersion, requires)
tests/                           DevHub.<X>.Core.Tests (xUnit), DevHub.TestKit (TempGitRepo, [GitFact])
tools/BoardDemo/                 writes a demo board + repository with every Board feature in use (README.md); never shipped
docs/                            GIT_CLIENT_PLAN.md (Git plugin), BOARD_PLAN.md (Board plugins), DIAGRAMS_PLAN.md (Diagrams
                                 plugins), TEMPLATE_FORMAT.md (the template spec)
docs/plugins/                    the plugin reference, one file per plugin family: update it when a plugin's actions,
                                 settings, files or hooks change
docs/diagrams/                   architecture diagrams (*.devhub-diagram.json): requirements, hooks, project
                                 references, Board and Diagrams extension points, the board's files, publishing
build/                           Get-PluginVersion, Pack-Plugin, Publish-Plugins, Test-Plugins (README.md)
Directory.Build.props            project kinds (plugin / .Core / test) and their Sdk references
Directory.Build.targets          after a build: plugin.json next to the DLLs, Properties/launchSettings.json (F5)
DevHub-Plugins.sln
```

## Plugin rules (details in DevHub's docs/PLUGINS.md)
- A plugin's UI project → the Sdk + its own `.Core`; a `.Core` → `DevHub.Sdk.Core` only (**no Avalonia**). All
  logic that can be tested without a window lives in a `.Core` with tests; ViewModels stay thin.
- A plugin registers everything in its plugin class's `ConfigureServices`; a switched-off plugin registers nothing.
  Its settings are its own section (`ISettingsSection`, `SectionId` = plugin id) in its `.Core`.
- **Hard requirement** (`requires` in plugin.json and `PluginInfo`): it may use the required plugin's types and
  reference its projects (GitHub → Git, Templates → Projects); the package leaves the required plugin's DLLs out.
- **Optional links** go through the hooks in the Sdk's `Integration/` (plain data only), injected as
  `IEnumerable<T>` or `T? x = null`, always with a fallback: `IProjectOpener`, `IProjectFileEditor`,
  `IProjectFolders`, `IAddFilesFromTemplate`, `IRepositoryEvents`, `ICommitMessageSuggester`, `IGitDecorator`,
  `IGitHubApi`. **Projects and Git never depend on each other** (either direction).
- A plugin's design tokens go in its `Themes/*.axaml` (returned from `IPlugin.Resources`); its `avares://` URIs name
  its own assembly. Use the Sdk's building blocks before writing new ones (Markdown, charts, ItemsDragDrop,
  PeoplePicker, …: PLUGINS.md "Building blocks").

## App rules (MVVM + UI; details in DevHub's docs/UI_GUIDE.md and the `avalonia-mvvm` skill)
- ViewModels never reference views, `Window`, `TopLevel` or controls; dialogs / pickers / status go through the
  Sdk's service interfaces. Code-behind = `InitializeComponent()` only. Compiled bindings (`x:DataType`) everywhere;
  XAML namespaces with `using:`.
- No hex colors, pixel sizes or font names in views: tokens via `DynamicResource`, matching the property's type
  (a number in `Padding` compiles but crashes).
- User operations are actions (`AppAction` + `IActionProvider`); screens are documents, tool windows or dialogs.
- Async disk / process work; never `.Result` / `.Wait()` on the UI thread. Every `+=` has its `-=`.
- Accessible names: list items override `ToString()`; icon-only buttons get `AutomationProperties.Name`.
- Text is translated (English and French; DevHub's UI_GUIDE §5 "Translate text"): `{l:T 'Apply'}` in XAML,
  `L.T("Apply")` / `L.T($"{n} issues")` in C#, whole sentences per case (no glued pieces). Ids, query syntax, git
  output and anything compared stay plain strings; a choice is matched by `SelectedIndex`, not its text. After
  changing text: `dotnet run --project tools/I18n -- keys plugins`, fill in `i18n/fr.json`, then `-- check plugins`
  (CI runs it). New boards get French names when DevHub is in French; existing boards keep theirs.

## Git client rules (Git plugin; details in docs/GIT_CLIENT_PLAN.md)
- Run git **only through `GitRunner`** (`DevHub.Git.Core`): `ArgumentList`, no shell string, `GIT_TERMINAL_PROMPT=0`;
  read-only commands pass `ReadOnly = true` (no optional locks).
- Parse **only machine-readable output** (`--porcelain=v2`, `-z`, explicit `--format`).
- **One repository per tab, one `GitRepositoryClient` per repository**; commands always name their repository.
- Anything that **changes** a repository happens only on a user action; destructive ones (discard, reset, delete
  an unmerged branch, force push) need a confirmation saying what is lost.
- Every command shows up in the Git Console (the runner logs it).
- Credentials come only from Git Credential Manager: never ask for, store or log a password or token (borrow it per
  API call with `credential fill`, run as `Sensitive`); the GitHub token only ever goes to api.github.com.
- Core git tests use real git in temp repos (`TempGitRepo`, `[GitFact]`); UI checks use scratch repositories,
  never the user's own.

## Board rules (Board plugins; details in docs/BOARD_PLAN.md)
- **Every change is an operation** (`IBoardOperation` in `DevHub.Board.Core`, a plain argument object) run through
  `BoardSession` / `BoardContext.RunAsync`: rules live in the operation (tested), ViewModels only build operations.
- **Stores are compare-and-swap:** a write names the version it's based on; a moved store means reload, never
  overwrite. The branch store (Board.Git) never touches the working tree, the index or HEAD.
- **Board needs nothing but itself:** Scrum, Git, Reports and Timeline plug in through `DevHub.Board`'s extension
  points (`IBoardPageProvider`, `IIssueSectionProvider`, `IIssueActionProvider`, `IBoardStoreProvider`); Board never
  references them.
- Files keep unknown fields and stable bytes (`BoardJson`); history is the per-issue change log (`history/*.jsonl`).
- Smart commits are shown and confirmed, never applied silently.

## Diagram rules (Diagrams plugins; details in docs/DIAGRAMS_PLAN.md)
- **Every change is an operation** (`IDiagramOperation` in `DevHub.Diagrams.Core`; `Apply` returns its undo), run
  through the editor (`IDiagramEditor.Do`): one Undo step each; the dirty dot comes from the undo stack.
- **A kind is two halves:** its model in a `.Core` (`IDiagramKindModel`: shapes, lines, the notation's rules, text
  exports, samples; no Avalonia, tested) and its drawing in the UI project (`IDiagramKind` over `IDiagramSurface`, so
  the canvas, PNG and SVG look the same). Kind plugins require Diagrams; Diagrams never references them.
- Files (`*.devhub-diagram.json`) keep unknown fields (`[JsonExtensionData]`); pictures go beside the file
  (`<name>.assets/`, relative paths).
- Exports are light (white, the course look) whatever the app's theme. Samples are built with `SampleBuilder` and
  `L.T`, so they come in the user's language; notation inside them stays plain text (no typographic spaces).
- Projects, Board and Diagrams meet only through DevHub's `Integration/` hooks (`IProjectDiagrams`, `IIssueLinks`,
  `IProjectCatalog`): each works alone.

## Template engine rules (Templates plugin; details in the `template-engine` skill and docs/TEMPLATE_FORMAT.md)
- **Unknown `{{...}}` tokens pass through untouched** (Vue, Handlebars, Jinja, Go templates, GitHub Actions `${{ }}`).
- **Never write outside the target root**: resolve every rendered path and reject anything that escapes it.
- **Generate into a staging folder, then move**: a failure must not leave a half-written project.
- **Binary files are copied byte for byte**; **never overwrite existing files silently** (conflicts are asked).
- **Post-create commands never run without the user seeing them first**; `ProcessStartInfo` with `ArgumentList`,
  `UseShellExecute = false`, never a shell string.
- Built-in templates are checked out byte for byte (`.gitattributes`); they're found next to the Templates DLL.

**Scope decisions (don't change without asking):** templates are a folder + `template.json` on disk; the
library lives in `%APPDATA%/DevHub/templates` (a setting may override it); no conditionals or loops in templates
(plain substitution); Windows first, but Core stays path-agnostic.

## Build / run / verify
```
dotnet build DevHub-Plugins.sln
dotnet test DevHub-Plugins.sln
```
- **Run a plugin in DevHub:** F5 with the generated "DevHub" profile (installed DevHub, your build folder through
  `DEVHUB_PLUGIN_PATHS`, its own data folder `.devhub-data/`). README.md, "Run and debug in DevHub".
- **Before publishing:** `pwsh build/Test-Plugins.ps1` (all plugins together, each alone with its requirements,
  next to DevHub's; fails on a crash, a hang, a problem or a skipped plugin).
- Core changes: add or extend tests (the template engine and the Git client are where regressions hurt files).
- UI changes: Claude can build but not see the window: drive it (UI Automation) or end with a **manual check list**.

## Versions and publishing (README.md)
- Each plugin has its own version, **N.M.P**: N = its highest `<id>/vN` tag (only the user creates those); each push on
  main touching `plugins/<Name>/` since adds 1 to P when it merges a `Fix/` (`Hotfix/`, `Bugfix/`) branch, else 1 to M
  (P back to 0). Name a branch `Fix/…` when it only fixes things. Every push to main publishes the plugins whose version changed (a
  release per plugin version + `index.json` on the `index` release); `"publish": false` in plugin.json skips one.
- A change that needs a newer Sdk is released in DevHub first (the Sdk on nuget.org), then here; DevHub's next
  release bundles the new plugin versions.

## Working with the user
The user is learning as they build DevHub: experienced in C# (Unity background), newer to Avalonia/XAML/MVVM and
desktop architecture. Explain **why** the first time a concept appears (Unity analogies help); keep steps small and
runnable. **Claude writes all the code** and **commits its own work on a feature branch**; the user merges (and
pushes) when needed: say in which order when DevHub and this repository both change. Keep DevHub's
`docs/ROADMAP.md` checkboxes current.
