# DevHub plugins: reference

What each plugin in this repository does, how to use it, what it stores, and what it offers other plugins. The Sdk
they build on is documented in DevHub's `docs/sdk/` (README, SDK_CORE.md, SDK.md); how plugins load, link and get
published is in DevHub's `docs/PLUGINS.md` and this repository's `README.md`.

| File | Plugins |
|---|---|
| [PROJECTS.md](PROJECTS.md) | Projects |
| [GIT.md](GIT.md) | Git |
| [GITHUB.md](GITHUB.md) | GitHub |
| [TEMPLATES.md](TEMPLATES.md) | Templates |
| [BOARD.md](BOARD.md) | Board, Board Scrum, Board Git, Board Reports, Board Timeline, Board Risks |
| [DIAGRAMS.md](DIAGRAMS.md) | Diagrams, UML Diagrams, Database Diagrams, Business Diagrams, Mood Boards |
| [CHARTS.md](CHARTS.md) | Charts |
| [CI.md](CI.md) | CI (GitHub Actions) |

Older design documents stay where they were: `docs/GIT_CLIENT_PLAN.md`, `docs/TEMPLATE_FORMAT.md` (the template
spec), `docs/BOARD_PLAN.md`, `docs/DIAGRAMS_PLAN.md`.

## Every plugin at a glance

| Plugin | Id | Requires | Category | Presets | Bundled |
|---|---|---|---|---|---|
| Projects | `devhub.projects` | – | Projects | Git client, Project templates | yes |
| Git | `devhub.git` | – | Version control | Git client | yes |
| GitHub | `devhub.github` | Git | Version control | Git client | yes |
| CI | `devhub.ci` | GitHub | Version control | Git client | |
| Templates | `devhub.templates` | Projects | Templates | Project templates | yes |
| Board | `devhub.board` | – | Planning | Planning | |
| Board Scrum | `devhub.board.scrum` | Board | Planning | Planning | |
| Board Git | `devhub.board.git` | Board, Git | Planning | Planning | |
| Board Reports | `devhub.board.reports` | Board | Planning | Planning | |
| Board Timeline | `devhub.board.timeline` | Board | Planning | Planning | |
| Board Risks | `devhub.board.risks` | Board | Planning | Planning | |
| Charts | `devhub.charts` | – | Tools | Planning | |
| Diagrams | `devhub.diagrams` | – | Tools | Planning | |
| UML Diagrams | `devhub.diagrams.uml` | Diagrams | Tools | Planning | |
| Database Diagrams | `devhub.diagrams.database` | Diagrams | Tools | Planning | |
| Business Diagrams | `devhub.diagrams.business` | Diagrams | Tools | Planning | |
| Mood Boards | `devhub.moodboard` | Diagrams | Tools | Planning | |

**Bundled** plugins come with DevHub's installer (`build/bundled-plugins.json` in DevHub) and then update through the
marketplace; the others are installed from Settings → Plugins → Marketplace. A plugin's **requirements** are
switched on and installed with it and load before it. Every plugin's settings are a section of DevHub's
`settings.json` under its id.

## Diagrams of this repository

Open them in DevHub (Diagrams + UML Diagrams plugins on), or from the Projects tool window's Diagrams group:

| `docs/diagrams/…` | Shows |
|---|---|
| Plugins and requirements | every plugin by category, and «requires» |
| Hooks between plugins | which plugin offers and which uses each `DevHub.Integration` hook |
| Plugin projects | every project and its project references |
| Board extension points | Board's stores, pages, issue sections and actions, and who implements them |
| Board data model | the files of a board as classes |
| Diagram kinds | the `IDiagramKind` extension point and the kinds that plug in |
| Publishing a plugin | from a push to an install in someone's DevHub |

### Three diagrams per plugin

Each plugin has a folder in `docs/diagrams/` with a **use case** diagram (who uses it for what, with «include» and
«extend»), an **activity** diagram of its main workflow (swimlanes, decisions with guards, loops) and a **class**
diagram of its main types. The class diagrams are read from the built DLLs, so every name, member, base type and
line matches the code at the time they were made: each type's own public members (up to 10 properties and 8
methods), grey boxes for what comes from the Sdk or another plugin, «uses» for a constructor parameter.

| Folder | Plugin | Activity |
|---|---|---|
| `Projects/` | Projects | open a project |
| `Git/` | Git | commit and push |
| `GitHub/` | GitHub | an API call |
| `Templates/` | Templates | new project |
| `Board/` | Board | an issue's life |
| `BoardScrum/` | Board Scrum | a sprint |
| `BoardGit/` | Board Git | a smart commit |
| `BoardReports/` | Board Reports | a release forecast |
| `BoardTimeline/` | Board Timeline | the roadmap |
| `BoardRisks/` | Board Risks | a risk |
| `Charts/` | Charts | make a chart |
| `Diagrams/` | Diagrams | edit a diagram |
| `DiagramsUml/` | UML Diagrams | checking the notation |
| `DiagramsDatabase/` | Database Diagrams | MCD to SQL |
| `DiagramsBusiness/` | Business Diagrams | a mind map |
| `MoodBoard/` | Mood Boards | a palette from a picture |
| `CI/` | CI | from push to re-run |

The files are `<Plugin> use cases`, `<Plugin> activity - <workflow>` and `<Plugin> classes` (`.devhub-diagram.json`).
They show the code as of 2026-10-19; after a big change to a plugin, update its diagrams in DevHub.

## Rules the plugins follow

- **Projects and Git never depend on each other**; they meet through hooks in `DevHub.Integration` (the Sdk), each
  with a fallback when the other is off.
- A plugin uses another plugin's types only when it **requires** it (GitHub → Git, Board Git → Board + Git, the
  Board and Diagrams families). Everything else is an optional hook.
- Logic lives in a `.Core` project without Avalonia, tested in `tests/`; the UI project holds ViewModels and views.
- Ids (actions, tool windows, documents, settings sections) are never translated: they're saved in settings.
