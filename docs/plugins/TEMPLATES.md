# Templates (`devhub.templates`)

Create new projects and files from templates with variables, or turn an existing folder into a template. Bundled
with DevHub. **Requires Projects** (it uses only Projects' hooks, no Projects types).

Code: `plugins/Templates/DevHub.Templates` and `DevHub.Templates.Core` (manifest, renderer, generation pipeline,
import; tested in `tests/DevHub.Templates.Core.Tests`). **The template format is specified in
[`docs/TEMPLATE_FORMAT.md`](../TEMPLATE_FORMAT.md)**: kinds, libraries, `template.json`, placeholders, steps, item
templates, built-ins, import, zip sharing. This page is the plugin around it.

## Using it

- **New Project…** (**Ctrl+N**, File menu, Welcome): pick a template (by language or category, filtered, searched),
  fill its variables, choose where (Settings → Templates decides where it starts), see every command that will run,
  generate into a staging folder, edit the **preview** (rename, move, delete, new folder, drag and drop, Undo,
  "Save to template"), then place it and run the post-create commands. **Raw** uses only a template's base command.
  Recent projects show on its start page.
- **Add Files from Template…** (File menu; right-click a project in Projects; a repository tab): an item template into
  an existing folder; conflicts get an Overwrite choice each; moves of existing files are spelled out. C# namespaces
  come from the nearest `.csproj`, `.asmdef` or Unity project.
- **Templates tool window** (left stripe, **Alt+2**, Welcome): every template at hand; double-click or Use starts New
  Project or Add Files.
- **Manage Templates** (File menu): New Template…, Edit, Use, Duplicate, Delete, Export / Import Zip. **One editor tab
  per template**: General, Variables, Base Command, Steps, Post-create, the content files (edited in your external
  editor), the raw `template.json`; Save (**Ctrl+S**), Revert, and a question before unsaved changes are lost.
- **Import Folder as Template…** (File menu): a folder becomes a template, with gitignore-style ignore rules and
  literal → variable replacement (a multi-word literal's other spellings become transforms).

Built-in templates (read-only; Duplicate to change): .NET (console, class library, solution with tests, Avalonia,
xUnit, add project to solution, class, interface), Unity (project via `@unity`, MonoBehaviour, ScriptableObject), Web
(Vite React / Vue / Svelte, Tauri), Rust, Python (package, module), Go, and an empty project. They ship next to the
plugin's DLL (`BuiltInTemplates/`).

## Actions and documents

| Id | Text | Shortcut | Menu |
|---|---|---|---|
| `Navigate.Generate` | New Project… | Ctrl+N | File, Welcome |
| `Navigate.AddFiles` | Add Files from Template… | | File |
| `Navigate.Templates` | Manage Templates | | File |
| `Navigate.Import` | Import Folder as Template… | | File |
| `Templates.NewTemplate` | New Template… | | File |
| `Templates.Save` | Save Template | Ctrl+S | File |
| `Templates.Reload` | Reload Templates | | File |
| `Templates.OpenLibraryFolder` | Open Templates Folder | | File |

Documents: Generate (New Project), AddFiles, Templates, Import (single tabs) and one template editor tab per template
(`TemplateEditorFactory`). Tool window: `templates` (left, Order 10).

## Settings (`devhub.templates`)

| Field | Meaning |
|---|---|
| `PersonalLibrary` | Your library's folder; null = `%APPDATA%\DevHub\templates`. |
| `ExtraLibraries` | More library folders (a team's git repository, a project's `templates/`). |
| `Author` | The `{{Author}}` built-in; null = the Windows user name. |
| `DefaultProjectFolder`, `ProjectFolderRules` | Where New Project starts: the most specific rule for the template's language and / or category, else the default folder, else the last one used. |
| `LastLocation`, `RecentProjects`, `GroupByLanguage` | Remembered choices. |

## For other plugins

| Offers | Hook | Used by |
|---|---|---|
| Open the Add Files tab for a folder | `IAddFilesFromTemplate` | Projects, Git |

| Uses (optional) | From | For |
|---|---|---|
| `IProjectOpener` | Git | opening the new project as a repository tab |
| `IProjectFolders` | Projects | default folders, a rescan so the project shows up |
| `IProjectFileEditor` | Projects | editing content files in the language's editor |

These are asked for when needed (`Func<IProjectOpener?>`), not injected into New Project: injecting them directly
would make a cycle through the shell's DocumentManager.

## Diagrams

`docs/diagrams/Templates/`: use cases, the activity of New Project (staging, preview, place, post-create), and the classes.
