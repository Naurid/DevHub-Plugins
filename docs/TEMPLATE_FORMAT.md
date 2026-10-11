# DevHub Template Format — v1

The single source of truth for what a template is. Code in the Templates plugin (`DevHub.Templates.Core`, planned) implements
this document; if they disagree, fix one of them in the same change.

## Library layout

```
%APPDATA%/DevHub/
  settings.json
  templates/
    project/
      <template-id>/
        template.json
        content/            ← copied into the target folder (names + contents rendered)
    file/
      <template-id>/
        template.json
        content/            ← usually one file, may be several (e.g. .h + .cpp)
```

`<template-id>` is lowercase kebab-case, unique within its kind, and equals the
`id` field in the manifest.

## template.json

```jsonc
{
  "schemaVersion": 1,
  "id": "unity-game",
  "kind": "project",                 // "project" | "file"
  "name": "Unity Game",
  "description": "URP project with asmdef-per-domain layout",
  "language": "C#",                  // free text, used for grouping/filtering
  "tags": ["unity", "game"],

  "variables": [
    {
      "name": "ProjectName",         // PascalCase identifier, [A-Za-z][A-Za-z0-9]*
      "label": "Project name",
      "type": "string",              // "string" | "bool" | "choice"
      "default": "MyGame",
      "required": true,
      "pattern": "^[A-Za-z][A-Za-z0-9_]*$",   // optional regex, string only
      "choices": []                  // required for "choice"
    }
  ],

  "postCreate": [
    {
      "name": "Initialize git",
      "command": "git",
      "args": ["init"],
      "workingDirectory": ".",       // relative to generated root
      "when": "InitGit",             // optional: name of a bool variable
      "continueOnError": false
    }
  ],

  "binaryExtensions": [".png", ".fbx"]  // optional, added to the built-in list
}
```

Unknown fields are preserved on load/save (don't drop data written by a newer
version). A missing or higher `schemaVersion` → refuse to load with a clear message.

## Placeholders

Syntax: `{{Name}}` or `{{Name:transform}}` — no spaces inside the braces.

| Transform | `My cool_game` → |
|---|---|
| *(none)* | `My cool_game` (as entered) |
| `pascal` | `MyCoolGame` |
| `camel`  | `myCoolGame` |
| `kebab`  | `my-cool-game` |
| `snake`  | `my_cool_game` |
| `upper`  | `MY COOL_GAME` |
| `lower`  | `my cool_game` |

Built-in variables (always available, can't be redeclared):
`Year`, `Date` (yyyy-MM-dd), `Author` (from settings), `Guid1`…`Guid9`
(each stable within one generation, so a GUID can be referenced in several files).

Rules:
1. Applied to **file contents** and to **every path segment** (folder and file names).
2. A token is substituted **only** if `Name` is a declared or built-in variable and
   the transform is known. Everything else (`{{ item }}`, `${{ secrets.X }}`,
   `{{#each}}`, `{{Unknown}}`) is left **exactly** as written.
3. Escape: `\{{` renders as a literal `{{` even if the name is valid.
4. `bool` variables render as `true`/`false`.
5. A rendered path segment that is empty, contains a path separator, `..`, or
   characters invalid for file names → generation fails with a validation error
   naming the variable.

## Files

- Binary detection: built-in extension list + manifest `binaryExtensions` + a
  NUL-byte sniff of the first 8 KB. Binary files are copied, never rendered
  (their names are still rendered).
- Text encoding and line endings are preserved as found (UTF-8 BOM kept if present).
- `template.json` itself is never copied into the output.

## Import (folder → template)

- Default ignore globs: `.git/`, `.vs/`, `.idea/`, `bin/`, `obj/`, `node_modules/`,
  `Library/`, `Temp/`, `Logs/`, `dist/`, `build/`, `__pycache__/`, `*.user`.
  The user can edit the list before importing.
- Optional literal → variable replacement: e.g. replace every occurrence of
  `LumberjackHollow` in names and contents with `{{ProjectName}}`. Applied only to
  text files; the preview lists how many hits per file.

## Future (not in v1)
Conditional blocks/files, zip export/import, remote template sources, template
inheritance.
