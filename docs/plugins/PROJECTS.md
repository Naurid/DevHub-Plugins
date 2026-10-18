# Projects (`devhub.projects`)

The Projects tool window: choose the folders you keep projects in, see the git repositories found in them with
their language, and open each one in the right tool. Bundled with DevHub. Requires nothing.

Code: `plugins/Projects/DevHub.Projects` (UI) and `DevHub.Projects.Core` (scanning, language detection, the tool
policy; tested in `tests/DevHub.Projects.Core.Tests`).

## Using it

- **Projects tool window** (left stripe, **Alt+1**, also on Welcome): the repositories found in your folders, grouped by
  folder. Double-click or Enter opens one: in a repository tab when Git is on (`IProjectOpener`), else in the
  project's external tool. Right-click a project: Open in DevHub (with Git), Open in External Tool, Open in
  Explorer, Add Files from Template… (with Templates), New Diagram… (with Diagrams). A folder: Remove Folder. With
  Diagrams, each project has a Diagrams group (open, show in Explorer, delete, refresh).
- **Add Folder…** (the tool window's header, Projects menu, Welcome): a folder to scan. Folders are scanned at startup and after adding
  one; **Rescan** (**F5**) scans again.
- **Language detection** from marker files at the repository's top: Unity, C#, Rust, Go, Tauri, JavaScript,
  TypeScript, Python, Java/Kotlin, C/C++.
- **Settings → Open With**: the tool per language. Defaults when you haven't chosen (`DefaultToolPolicy`): Unity →
  Unity Hub, C# → Rider, the others → VS Code, each only when installed, else the program Windows associates with
  the folder. Choices: OS default, Explorer, VS Code, Rider, Visual Studio, Unity Hub, a custom program.

## Actions

| Id | Text | Shortcut | Where |
|---|---|---|---|
| `Repositories.AddFolder` | Add Folder… | | tool window header, Projects menu, Welcome |
| `Repositories.Rescan` | Rescan | F5 | tool window header, Projects menu |

The tool window's own toggle (Alt+1) comes from the shell.

## Settings (`devhub.projects`)

| Field | Meaning |
|---|---|
| `RepositoryFolders` | The folders the tool window scans. |
| `LanguageTools` | Language id → the chosen tool (`LanguageToolChoice`); missing languages use the default policy. |

Before the plugin split these were top-level fields of `settings.json`; the settings store moves them into this
section on first load.

## For other plugins

| Offers | To | Hook |
|---|---|---|
| Open a file in the editor chosen for its project's language | Git (Open in Editor, F4), Templates | `IProjectFileEditor` |
| The project folders and a rescan | Git (Clone into a project folder), Templates | `IProjectFolders` |
| The list of projects | Diagrams (Link to Project…) | `IProjectCatalog` |

| Uses (optional) | From | For |
|---|---|---|
| `IProjectOpener` | Git | opening a repository tab on double-click |
| `IAddFilesFromTemplate` | Templates | "Add Files from Template…" on a project |
| `IProjectDiagrams` | Diagrams | the Diagrams group under each project |

Its own services (`IRepositoryScanner`, `IProjectLanguageDetector`, `IToolLocator`, `IProjectLauncher`) are for itself;
Templates requires Projects but uses only the hooks above.
