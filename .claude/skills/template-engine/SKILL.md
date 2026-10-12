---
name: template-engine
description: Rules and pitfalls for the Templates plugin engine (DevHub.Templates.Core) — manifest model, placeholder renderer, project generator, file-template stamping, folder importer, ignore globs, post-create command runner. Use when writing, changing, reviewing or testing anything under plugins/Templates/DevHub.Templates.Core or tests/DevHub.Templates.Core.Tests (and engine-style logic in any plugin .Core project).
---

# Templates plugin — template engine (DevHub.Templates.Core)

`docs/TEMPLATE_FORMAT.md` is the spec. Read the relevant section before changing
behavior; update it in the same change if behavior changes.

## Design rules
- **No UI types.** A plugin .Core project never references Avalonia. Report progress with
  `IProgress<T>`, stream output with `IAsyncEnumerable<T>` or callbacks, accept
  `CancellationToken` on anything that does I/O or runs processes.
- **Filesystem behind an abstraction only if it's needed for tests.** Prefer
  real temp directories in tests (`Directory.CreateTempSubdirectory()`, cleaned up
  in `Dispose`) over mocking `System.IO` — the engine's bugs live in real path
  and encoding behavior.
- **Return results, don't throw, for user-caused problems.** Validation errors
  (bad variable value, path conflict, invalid manifest) → a result/error list the
  UI can show. Exceptions are for bugs and unexpected I/O failures.
- Immutable models where practical (`record`s for manifest data, variable values).

## Renderer pitfalls
- Tokenize, don't `string.Replace` in a loop — sequential replaces re-render
  already-substituted values (a value containing `{{Other}}` would expand).
  Substituted values are **never** re-scanned.
- Unknown names/transforms → emit the original token text verbatim.
- `\{{` escape → literal `{{`, backslash consumed.
- Case transforms must split words on spaces, `_`, `-`, and case boundaries
  (`myHTTPServer` → `my`, `HTTP`, `Server`). Test acronyms and digits.
- `Guid1..9` are generated once per render session, not per occurrence.

## Generator pitfalls
- Flow: validate inputs → render all paths → check path-escape + conflicts →
  write everything into a staging dir → move into the target → only then report success.
- Path-escape check: `Path.GetFullPath(Path.Combine(root, rel))` must start with
  `Path.GetFullPath(root) + Path.DirectorySeparatorChar` (use
  `StringComparison.OrdinalIgnoreCase` on Windows).
- Moving the staging dir: if the target exists and is empty, move the contents,
  not the folder. Staging dir should be on the same volume as the target
  (sibling folder), otherwise `Directory.Move` fails across drives.
- Encoding: detect BOM, read with that encoding, write it back the same way.
  Don't normalize line endings.
- Case-insensitive filesystems: two rendered paths differing only by case are a
  conflict on Windows.

## Import pitfalls
- Glob matching: support `dir/`, `*.ext`, `**/name`. Match on paths relative to
  the import root with `/` separators. Put the matcher behind its own class with
  exhaustive tests.
- Skip symlinks/junctions (don't follow them out of the tree).
- Literal replacement applies to text files only, and to path segments.

## Command runner pitfalls
- `UseShellExecute = false`, `ArgumentList` (never a joined argument string),
  `RedirectStandardOutput/Error`, read both streams asynchronously to avoid
  deadlocks on full buffers.
- Windows: `npm`, `npx`, `yarn`, `pnpm` are `.cmd` shims. Resolve the command via
  `PATH` + `PATHEXT`; for `.cmd`/`.bat`, run `cmd.exe /c` with the command
  and args as separate `ArgumentList` entries.
- Cancellation kills the whole process tree (`Process.Kill(entireProcessTree: true)`).
- `when` gates on a bool variable; `continueOnError` false → stop the remaining commands.

## Tests expected per change
Every behavior in the spec gets at least one test. When fixing a bug, first write
a test that reproduces it. Name tests `Method_Scenario_Expected`.
