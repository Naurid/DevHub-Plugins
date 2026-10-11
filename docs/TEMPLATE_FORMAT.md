# DevHub Template Format — v1

The single source of truth for what a template is and how it's applied. The Templates plugin
(`plugins/DevHub.Templates.Core` + `plugins/DevHub.Templates`) implements this document; if
they disagree, fix one of them in the same change.

Decisions confirmed 2026-10-11:
- A project template is assembled from optional parts: a **base command**, content on top,
  **steps** that rearrange, and **post-create commands**.
- Two kinds: **project** (creates a new folder) and **item** (goes into an existing folder).
- Templates are organised by **one language and any number of categories**, in **several
  libraries**, plus read-only **built-ins** that can be duplicated.
- Generation runs in a **staging folder**, then a **preview you can edit** (with "Save to
  template"), then moves into place.

## 1. Kinds

| Kind | What it does | Examples |
|---|---|---|
| `project` | Creates a new project folder under a parent folder you choose. | raw `dotnet new console`; a Unity game with your layout; a Vite app with your configs on top |
| `item` | Adds files and/or folders into an existing folder you choose, and can move files already there. | a C# class; a MonoBehaviour; a `.h` + `.cpp` pair; a folder structure applied to an existing project |

Every part is optional. Which parts a template has decides what it is:

| Parts | Result |
|---|---|
| base command only | a **raw project**: exactly what the tool's own scaffold command makes (any template with a base command can also be used raw: the **Raw** option skips its content, steps and post-create commands) |
| content only | a **folder template**: your folders and starter files |
| base command + content + steps | the tool's project, with your files on top, rearranged your way |
| item: content (+ move steps) | a script / file set / folder structure for an existing project |

## 2. Libraries

```
<library>/
  <template-id>/
    template.json         the manifest (§3)
    content/              copied into the result, names and contents rendered (§5)
```

| Library | Where | Editable |
|---|---|---|
| **Personal** | `%APPDATA%/DevHub/templates/` (a setting may move it) | yes |
| **Extra libraries** | folders you add in Settings → Templates (a git repo of team templates, a project's `templates/` folder) | yes, if writable |
| **Built-in** | shipped with the Templates plugin (`BuiltInTemplates/` next to DevHub), updated with DevHub | no: **Duplicate** copies one into a writable library; **Save to Template** from a preview saves into such a copy in Personal |

`<template-id>` is lowercase kebab-case, unique within its library, and equals the `id` field.
The same id in two libraries is allowed; the reference is `<library>/<id>`. Templates aren't
grouped on disk by kind or language: organising is done by the manifest's `language` and
`categories`.

## 3. template.json

```jsonc
{
  "schemaVersion": 1,
  "id": "unity-game",
  "kind": "project",                    // "project" | "item"
  "name": "Unity Game",
  "description": "URP project with asmdef-per-domain layout",
  "language": "C#",                     // one language; "Any" for language-neutral templates
  "categories": ["Unity", "Game"],      // any number; the browser groups / filters by them

  "variables": [
    {
      "name": "Name",                   // PascalCase identifier, [A-Za-z][A-Za-z0-9]*
      "label": "Project name",
      "type": "string",                 // "string" | "bool" | "choice"
      "default": "MyGame",
      "required": true,
      "pattern": "^[A-Za-z][A-Za-z0-9_]*$",   // optional regex, string only
      "choices": []                     // required for "choice"
    }
  ],

  "rootFolder": "{{Name}}",             // project only: folder created under the chosen parent

  "baseCommand": {                      // project only, optional (§4 step 1)
    "command": "dotnet",
    "args": ["new", "console", "-n", "{{Name}}", "-o", "."],
    "runIn": "root"                     // "root": inside the empty root folder; "parent": the command creates the root
  },

  "steps": [                            // optional, run in order (§6)
    { "op": "createFolder", "path": "src" },
    { "op": "move", "from": "*.cs", "to": "src/{{Name}}/" },
    { "op": "rename", "from": "src/{{Name}}/Program.cs", "to": "Main.cs" },
    { "op": "delete", "path": "README.md" }
  ],

  "postCreate": [                       // optional, run in the final folder (§4 step 6)
    { "name": "Initialize git", "command": "git", "args": ["init"],
      "workingDirectory": ".", "when": "InitGit", "continueOnError": false }
  ],

  "binaryExtensions": [".png", ".fbx"]  // optional, added to the built-in list
}
```

Unknown fields are preserved on load and save. DevHub writes the file for people to read: indented,
without escapes (`<Name>`, not `<Name>`), and without values that are the default anyway
(false, empty lists, `runIn: "root"`); `kind` and a step's `op` are always written. Comments in a
hand-written file don't survive a save from the editor. A missing or higher `schemaVersion` is refused
with a clear message. Commands never go through a shell: `command` is an executable and `args`
are separate arguments (`ArgumentList`), both rendered (§5). `command` may be a DevHub tool
alias such as `@unity` (the Unity editor version you pick).

## 4. Generating a project

1. **Stage:** an empty staging folder; the `baseCommand` runs there (in the root, or its parent).
   Afterwards the root folder must exist.
2. **Overlay:** `content/` is copied into the root, rendered. The template's file replaces a file
   the command made.
3. **Steps** run in order (§6).
4. **Preview:** the final tree (new / from the command / moved). You can move, rename, delete and
   add folders for this generation; **Save to template** appends those edits as `steps`
   (writable libraries only).
5. **Place:** conflict check, then the root moves into the chosen parent. An existing non-empty
   folder means nothing is written and you decide.
6. **Post-create** commands run in the placed folder, output shown live.

Before step 1, DevHub lists every command the generation will run (rendered); nothing runs
until you agree. A failure or cancel in steps 1–4 deletes the staging folder; the target is
never touched.

## 5. Placeholders

Syntax: `{{Name}}` or `{{Name:transform}}`, no spaces inside the braces. In paths (content file
and folder names, `rootFolder`, step paths) `{{Name.transform}}` works too: Windows doesn't allow
`:` in a file name, so a content file is called `{{Name.kebab}}.md`.

| Transform | `My cool_game` → |
|---|---|
| *(none)* | `My cool_game` |
| `pascal` | `MyCoolGame` |
| `camel`  | `myCoolGame` |
| `kebab`  | `my-cool-game` |
| `snake`  | `my_cool_game` |
| `upper`  | `MY COOL_GAME` |
| `lower`  | `my cool_game` |

pascal / camel / kebab / snake split the value into words on spaces and other non-alphanumerics
(`_`, `-`, `.`) and on case changes. Acronyms stay together and digits stay with the word before
them: `myHTTPServer` → my, HTTP, Server (`MyHttpServer`); `version2Beta` → version2, Beta.
upper / lower change the whole value as is. Variable names are case-sensitive.

Built-ins (can't be redeclared): `Year`, `Date` (yyyy-MM-dd), `Author` (from settings),
`Guid1`…`Guid9` (stable within one generation).

1. Applied to file contents, every path segment, `rootFolder`, step paths, and commands.
2. Only declared or built-in names with a known transform are substituted; everything else
   (`{{ item }}`, `${{ secrets.X }}`, `{{#each}}`, `{{Unknown}}`) stays **exactly** as written.
3. `\{{` renders a literal `{{`.
4. `bool` renders as `true` / `false`.
5. A rendered path segment fails with an error naming the variable when it:
   - is empty, contains a separator, or is `.` or `..`
   - has characters Windows forbids (`<>:"|?*`, control characters)
   - ends in a dot or space
   - is a reserved device name (`CON`, `NUL`, `COM1`…)

   Step paths may contain the globs `*` and `?`.

## 6. Steps

| Op | Fields | Does |
|---|---|---|
| `createFolder` | `path` | creates the folder (and parents) |
| `move` | `from` (path or glob), `to` | `to` ending in `/` = a folder (created) keeping names; else the new path of one match |
| `rename` | `from` (path), `to` (a name) | renames in place |
| `delete` | `path` (path or glob) | deletes files / folders |

Paths are relative to the root (item: the target folder), use `/`, and are rendered. Globs:
`*`, `**`, `?`. A glob matching nothing is fine; a missing plain path is an error. Every resolved
path must stay inside the root. Item templates may move files already in the target (e.g.
`*.cs` into `src/`), shown in the preview first.

A `move` into a folder creates it only when something moves. A folder that a move leaves empty is
removed (only `createFolder` makes empty folders).

## 7. Applying an item template

You pick the target folder. The result is computed in staging (content rendered, then steps),
then previewed: what's added and moved, with conflicts marked (overwritten only if you choose).
Edits work as in §4 step 4. Then it's written into the target and `postCreate` runs there.
Version control and generated folders (`.git`, `.vs`, `.idea`, `node_modules`, `Library`, `Temp`,
`obj`, `bin`) aren't shown and steps can't touch them. Staging is a hidden `.devhub-staging-*`
folder inside the target (same drive, so moves are instant), removed afterwards.
A variable named `Namespace` starts with the folder's C# namespace: the nearest `.csproj` (`RootNamespace`, else
the project's name), `.asmdef` (`rootNamespace`) or Unity project (its name; folders under `Assets`, without
a leading `Scripts`), plus the folders in between (`src/Player` under `Game.csproj` → `Game.src.Player`).
The preview lists files already there (each overwritten only if you check it), your files that move or
are deleted, and the post-create commands, which run after Apply.

## 8. Files

Binary detection uses the built-in extension list, `binaryExtensions`, and a NUL byte in the first
8 KB. Binary files are copied byte for byte (names still rendered). Encoding and line endings are
kept (a UTF-8 BOM stays). `template.json` is never copied into the output.

## 9. Built-in templates (read-only; Duplicate to change)

| Language | Templates |
|---|---|
| C# / .NET | console, class library, solution (+ add a project), Avalonia app, xUnit test project |
| C# / Unity | new Unity project (`@unity`, editor version from the installed ones) |
| TypeScript / Web | Vite (React / Vue / Svelte, TS), Tauri app |
| Rust | `cargo new` (binary / library) |
| Python | venv + `src/<name>` + `pyproject.toml` |
| Go | `go mod init` + `main.go` |

## 10. Import (folder → template)

Default ignore globs: `.git/`, `.vs/`, `.idea/`, `bin/`, `obj/`, `node_modules/`, `Library/`,
`Temp/`, `Logs/`, `dist/`, `build/`, `__pycache__/`, `*.user` (editable before importing).
Ignore patterns work like `.gitignore`: `name` matches at any depth, `name/` only folders, a pattern
with a slash inside (`/out`, `src/gen/`) a path from the top; `*`, `**`, `?`; `#` comments. Left-out
folders aren't looked into.

Optional literal → variable replacement (e.g. `LumberjackHollow` → `{{Name}}`) in names and text
contents, with hits per file in the preview. A literal of several words is also found in its other
spellings, each becoming that transform (`lumberjack-hollow` → `{{Name:kebab}}`, in file names
`{{Name.kebab}}`); a one-word literal only as typed. Longest first, one pass. Text that would already
render (a `{{Name}}` in a Handlebars file, Name being a variable) is escaped as `\{{Name}}`. Binary
files and non-UTF-8 text without a BOM are copied as they are. Two files that would get the same
name stop the import before anything is written.

## 11. Not in v1

Conditional blocks and files; template inheritance; remote sources (other than a library folder
that's a git checkout); scripts as steps (use `postCreate`); zip export / import (polish).
