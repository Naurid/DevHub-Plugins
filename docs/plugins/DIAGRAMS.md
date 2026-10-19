# Diagrams and its kinds

A diagram editor in DevHub: draw, check and export diagrams (PNG, SVG, copy image, and text such as PlantUML,
Mermaid or SQL), linked to your projects and boards. The **Diagrams** plugin is the engine and the editor; the
**kinds** come from the plugins that require it. Design and history: [`docs/DIAGRAMS_PLAN.md`](../DIAGRAMS_PLAN.md).

| Plugin | Id | Requires | Kinds (`Diagram.Kind`) |
|---|---|---|---|
| Diagrams | `devhub.diagrams` | – | the editor; no kinds of its own |
| UML Diagrams | `devhub.diagrams.uml` | Diagrams | use case (`usecase`), activity, class, object, sequence, state machine (`state`), component, deployment, package |
| Database Diagrams | `devhub.diagrams.database` | Diagrams | Merise MCD (`merise`) → MLD, crow's-foot ERD (`erd`) |
| Business Diagrams | `devhub.diagrams.business` | Diagrams | flowchart, BPMN (`bpmn`), mind map (`mindmap`) |
| Mood Boards | `devhub.moodboard` | Diagrams | mood board (`moodboard`) |

Code: `plugins/Diagrams/DevHub.Diagrams.Core` (model, file format, operations and Undo, routing, snapping, hit tests,
auto layout, rules framework, project links; no Avalonia), `DevHub.Diagrams` (canvas, tab, palette, properties,
export), and per kind plugin a `.Core` (shapes, lines, rules, text exports, samples) and a UI project (drawing,
property fields, commands). Tests: `tests/DevHub.Diagrams.Core.Tests`, `DevHub.Diagrams.Database.Core.Tests`,
`DevHub.Diagrams.Kinds.Tests`.

## Using it

- **New Diagram…** (File menu, Welcome): a kind, a name, "Start from" a sample (course examples in English and
  French, and a showcase of every shape), and where: a project (its `docs/diagrams/`) or a folder. **Open
  Diagram…** opens a `*.devhub-diagram.json`. Each file is a tab (`Diagram:<path>`).
- **The canvas**: pan and zoom, grid, select (rubber band), move with guides and snapping, resize, connect (lines
  routed straight or at right angles around shapes), bends, in-place text, align and arrange, copy / paste, Undo /
  Redo, a minimap. Containers (system boundaries, swimlanes, packages, pools, frames) take shapes inside them.
- **Line labels** can be dragged anywhere (the selected line's label has a dashed frame). A label keeps its place
  along the line and its distance from it, so it follows when the shapes move; each drag is one Undo step.
- **Palette** (the kind's shapes and lines), **Properties** (the kind's fields; fill, line and text colour, text size,
  bold, line width, dash and arrowheads on every shape and line, and a **Label background** on lines), **Problems**
  (the kind's notation rules: errors, warnings, hints; click one to select what it's about).
- **Colours**: every colour picker has **Transparent** (no fill, no label background) and **Default**. A shape with its
  own fill and no text colour gets text that contrasts with the fill, so light fills stay readable in the dark theme;
  see-through fills (#AARRGGBB) read well in both themes.
- **Packages**: an empty package has its name in its body, wrapped; one with shapes inside has it in its tab, which
  grows up to the package's width. A name that still doesn't fit ends in "…" and shows in full on hover and above the
  package while it's selected.
- **Export**: PNG, SVG, copy image, and the kind's text exports. Settings → Diagrams decides where images go: ask
  every time, next to the diagram (optionally in a subfolder), or one folder; exporting again replaces the image so
  documents linking to it stay current.
- **Links**: **Link to Project…** (the projects Projects knows, or a folder; a diagram belongs to a project when it
  lies in its folder or links to it), and with Board a shape **linked to an issue** shows its status and opens it;
  **Create Story from This Shape** makes one.

## The kinds

**UML Diagrams**: the course's notation rules are checked (e.g. «extend» goes from the extension to the base use case,
an activity has one start, multiplicities are well-formed, no inheritance loops).

| Kind | Shapes and lines | Text exports |
|---|---|---|
| Use case | actors (stereotypes), use cases (extension points), system boundary; association, «include», «extend», generalization | PlantUML, Mermaid |
| Activity | actions, start / end / flow end, decisions with guards, fork / join, objects, signals, swimlanes, activities | PlantUML, Mermaid |
| Class / object | classes (attributes and operations typed as text lines, edited as forms), interfaces, enumerations, datatypes, packages; association (roles, multiplicities, navigability), aggregation, composition, inheritance, realization, dependency; objects and links | PlantUML, Mermaid |
| Sequence | lifelines, actors, activations, sync / async / reply / create messages, fragments (loop, alt, opt) | PlantUML, Mermaid |
| State machine | states with activities, composite states, choice, fork / join, history, guarded transitions | PlantUML, Mermaid |
| Component, deployment, package | components, interfaces, ports, artifacts, databases; nodes, devices, execution environments; packages; dependency, assembly, realization, «deploy», «import» | PlantUML |

**Database Diagrams**: a Merise MCD (entities, associations, cardinalities, identifiers) with **Generate MLD**
(the MCD → MLD rules), the table diagram (MLD look or crow's foot), SQL for MySQL, PostgreSQL, SQL Server and SQLite
(course type names mapped per database), **SQL import** of CREATE TABLE scripts into an MLD and a rebuilt MCD, the
data dictionary (CSV) and the MLD as text.

**Business Diagrams**: flowcharts (ISO shapes; Mermaid), BPMN (pools, lanes, events with triggers, typed tasks,
sub-processes, gateways, data, sequence and message flows with the pool rules; Markdown outline), mind maps
(coloured curved branches, Add Idea, a two-sided tree layout; Markdown, Mermaid).

**Mood Boards**: pictures (dropped, pasted or picked; copied beside the file into `<board>.assets/`, named by
content), colour swatches and **Palette from Picture**, shapes and frames, link cards, text, a freehand pen and
eraser; CSS and Markdown exports.

## Actions

| Id | Text | Menu |
|---|---|---|
| `Diagrams.New` | New Diagram… | File, Welcome |
| `Diagrams.Open` | Open Diagram… | File |

Kinds add their own commands to the editor's toolbar (Generate MLD, Palette from Picture, Add Idea…).

## Settings (`devhub.diagrams`, Settings → Diagrams)

| Field | Meaning |
|---|---|
| `ExportPlace` | `Ask` (default), `BesideDiagram`, `Folder`. |
| `BesideSubfolder` | With BesideDiagram: a subfolder ("images"), or null. |
| `ExportFolder` | With Folder: the folder. |

## The file format

`<name>.devhub-diagram.json`: `schemaVersion` (1), `kind`, `title`, `project` (the project folder relative to the
file, "../.." for one in `docs/diagrams/`), `props`, `nodes` (id, type, x, y, width, height, rotation, text, parentId,
props, points) and `edges` (id, type, from, to, text, routing, waypoints, labelAt, props). What a type means belongs
to the kind; fields this version doesn't know are kept and written back. A file with a newer schema opens
read-only.

## Writing a kind (the `IDiagramKind` extension point)

Diagram: `docs/diagrams/Diagram kinds`.

1. In the kind plugin's `.Core`, implement **`IDiagramKindModel`**: `Id` (stored in the file), `Name`, `Description`,
   `NodeTypes` (`NodeTypeInfo`: type, name, shape, default size and text, container or not, connectable, default
   props), `EdgeTypes` (`EdgeTypeInfo`: which node types it may join, default routing and text), `Rules`
   (`IDiagramRule.Check(diagram)` → `DiagramProblem`s), `TextExporters` (`IDiagramTextExporter`: name, extension,
   `Export(diagram)`), `Samples` (`DiagramSample`, built with `SampleBuilder`), `Create(title)`, and optionally a
   `CustomPath` for lines drawn your own way. Test it there.
2. In the UI project, derive from **`DiagramKindBase`** (which implements `IDiagramKind`): `Model`, `IconKind`, `Order`
   (position in New Diagram), `DrawNode` / `DrawEdge` on an `IDiagramSurface` (Avalonia, SVG and measuring surfaces
   share it), `NodeFields` / `EdgeFields` / `DiagramFields` (`PropertyField`s: text, multiline, choice, toggle,
   colour, number or slider, rows), `Fit` (size a shape to its text), `Commands` (`KindCommand`s run against an
   `IDiagramEditor`), and the pen, paste and file-drop hooks for kinds that need them. Draw line labels with
   `Label(s, text, LabelAnchor(d, e, path), …, edge: e)` so they take the line's label background and drag; a kind
   that places labels its own way (above a sequence message, on a mind map branch) overrides **`LabelAnchor`**. A kind
   that cuts a text short (`Folder`'s "…") reports it through **`HiddenText`**, which the canvas shows on hover.
3. Register it: `services.AddSingleton<IDiagramKind, MyKind>()`, and require `devhub.diagrams`.

Every change to a diagram is an **`IDiagramOperation`** (`Apply` returns its undo), run through `IDiagramEditor.Do` as
one Undo step.

## For other plugins

| Offers | Hook | Used by |
|---|---|---|
| A project's diagrams, the ones mentioning an issue, open, create | `IProjectDiagrams` | Projects (Diagrams under each project), Board (Diagrams page and issue section) |

| Uses (optional) | From | For |
|---|---|---|
| `IProjectCatalog` | Projects | Link to Project… |
| `IIssueLinks` | Board | shapes linked to issues, Create Story |
| `IExportService`, `IDialogService`, `IStatusService` | the shell | export, pickers, messages |

## Diagrams

Three per plugin (use cases, an activity, the classes): `docs/diagrams/Diagrams/` (editing a diagram), `DiagramsUml/` (how a change is checked), `DiagramsDatabase/` (MCD to SQL), `DiagramsBusiness/` (a mind map), `MoodBoard/` (a palette from a picture).
