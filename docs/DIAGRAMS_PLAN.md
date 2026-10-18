# Diagrams plan (DevHub roadmap milestone D)

Source: the course "Principes d'analyse informatique" (slides and exercises read 2026-10-18). Status: DevHub's
`docs/ROADMAP.md`, section D (stage DGn = roadmap Dn).

## Context
Roadmap milestone **D** (DevHub `docs/ROADMAP.md`, decided 2026-10-12: diagrams export images and diagram text, link
to the board). D0 asked for the course PDFs: the user provided them (course "Principes
d'analyse informatique", K. Belouh: slides on use cases, activity diagrams, class diagrams part 1, exercises with
solutions, and use case examples hôpital / distributeur / coaching). The course outline asks students to *choose,
build and represent data, processing and behaviour models with an adapted notation*. The user added, 2026-10-18:
**relational database modelling** (chosen: **Merise MCD → MLD, with SQL export**) and **mood boards** (images,
colour swatches and shapes, links and freehand drawing; no sticky notes).

Outcome: a diagram editor tab in DevHub where a student draws the course's diagrams with the course's notation,
gets warnings when a rule is broken, and exports PNG / SVG / copy image (and PlantUML / Mermaid / SQL text) for the
dossier; plus a free mood board on the same canvas.

## Notation rules taken from the PDFs (D0 result)
- **Use case**: actor = stick figure with name, outside the system; «stereotype» actor variant (e.g. «Service»
  Authentication, small figure + text); use case = ellipse; system boundary = rectangle titled with the system name;
  association = plain line actor–use case; generalization (use cases or actors) = solid line + hollow triangle on the
  general one; «include» = dashed arrow base → included; «extend» = dashed arrow extension → base, base may show an
  "extension points" compartment (line across the ellipse). Primary actors left, secondary (services) right.
  Rules (warnings): actor inside the boundary; extend/include drawn the wrong way; include/extend between an actor and
  a use case; use case with no actor *is allowed* (exercise: internal use cases).
- **Activity**: start = filled dot (one or more); end = bullseye (one or more); local end (flow final) = circled ×;
  action = rounded rectangle; activity = large rounded rectangle containing actions; decision / merge = diamond with
  [guards] on outgoing flows; loops = flow back through a decision; fork / join = thick bar (horizontal or vertical);
  object node = rectangle linked to an action (object flow); send signal = pentagon pointing right, receive signal =
  rectangle with a notch on the left; partitions (swimlanes) = labelled horizontal or vertical lanes. Rules: decision
  branches without guards, fork without a matching join (info), flows into a start / out of an end, unreachable nodes.
- **Class**: rectangle with name compartment (+ optional «enumeration» / «datatype» / «interface» stereotype,
  {properties}, *italic* = abstract), attributes `vis name : Type [mult] = default`, operations
  `vis name(arg : Type = def) : Return`; visibility + public, # protected, - private; underlined = class scope;
  enumeration literals compartment; compartments may be hidden; notes = dog-eared rectangle tied by a dashed line to one
  or more elements. Relations (roadmap D4): association (name, role, multiplicities at both ends), aggregation (hollow
  diamond), composition (filled diamond), generalization (hollow triangle), realization (dashed + hollow triangle),
  dependency (dashed arrow).
- **Merise MCD**: entity = rectangle with name and properties, identifier underlined; association = rounded rectangle
  (or ellipse) with a verb name and optional properties; each leg carries a cardinality 0,1 / 1,1 / 0,n / 1,n; binary,
  n-ary and reflexive associations. **MLD** derived automatically: entity → table, identifier → primary key; leg (x,1)
  → foreign key (#) in that entity's table, the association's properties move with it; (x,n)–(x,n) and n-ary → a
  junction table (PK = the FKs + its own properties); 1,1–1,1 → FK on one side (user picks). Data dictionary (name,
  meaning, type, length, constraints) per property.

## Scope widened (2026-10-18)
The course is a guideline; the target is what Visual Paradigm draws: every usual UML diagram, database diagrams
(Merise and crow's-foot ERD), business diagrams (flowchart, BPMN, mind map), and formatting (fill, line and text
colour, text size, bold) on every shape and line.

## Plugins (as built)
One engine, kinds plug in (the Board pattern).
- `plugins/Diagrams/DevHub.Diagrams.Core` (no Avalonia, tested): the model (`Diagram`, `DiagramNode`, `DiagramEdge`,
  props as strings, unknown fields kept), `*.devhub-diagram.json` with `schemaVersion` + `kind`, every change an
  operation (`IDiagramOperation`, `Apply` returns its undo; `UndoStack`), geometry (outline intersection, orthogonal
  routing around shapes, snapping and guides, hit tests, layered auto layout), `IDiagramKindModel` (shapes, lines,
  rules, text exporters, samples, custom paths), `SampleBuilder`, project links (`docs/diagrams`, relative links).
- `plugins/Diagrams/DevHub.Diagrams` (UI): `DiagramCanvas` (pan / zoom, grid, select, rubber band, move with guides
  and reparenting, resize, connect, bends, in-place text, pen / eraser, file drops), `DiagramMinimap`, one tab per file
  (`Diagram:<path>`), palette, properties, problems, toolbar (align, arrange, export PNG / SVG / copy image / text,
  kind tools, issue link, project link), New Diagram (kind, "Start from" sample, project or folder). Surfaces:
  Avalonia, SVG, measure. Extension point `IDiagramKind`.
- `plugins/DiagramsUml` (+ `.Core`): use case, activity, class, object, sequence, state machine, component,
  deployment, package; the course's rules; PlantUML and Mermaid.
- `plugins/DiagramsDatabase` (+ `.Core`): Merise MCD with the MCD → MLD rules (`Mld.ToSchema`), Generate MLD, the
  table diagram (MLD look or crow's foot), SQL for MySQL / PostgreSQL / SQL Server / SQLite (`SqlWriter`, course type
  names like Entier / Alphanumérique(50) / Compteur mapped per database), CREATE TABLE import (`SqlImport`, also
  ALTER TABLE … FOREIGN KEY, `GO`, quoted names) into an MLD and a rebuilt MCD, the data dictionary (CSV), MLD text.
- `plugins/DiagramsBusiness` (+ `.Core`): flowchart (ISO shapes, Mermaid), BPMN (pools, lanes, events with triggers,
  typed tasks, sub-processes, gateways, data, sequence / message flows with the pool rules, Markdown outline), mind
  map (coloured curved branches, Add Idea, two-sided tree arrange, Markdown / Mermaid).
- `plugins/MoodBoard` (+ `.Core`): pictures copied beside the board (`<board>.assets/`, named by content), colour
  swatches, Palette from Picture (k-means), shapes, frames, text, link cards, freehand strokes (simplified), CSS /
  Markdown exports.
- Hooks (DevHub's `Integration/`, plain data): `IProjectDiagrams` (Diagrams → Projects tree "Diagrams" group, Board's
  Diagrams page and issue section), `IIssueLinks` (Board → shapes linked to issues, Create Story), `IProjectCatalog`
  (Projects → Link to Project).

## Languages
Every text in English and French (`L.T` / `{l:T}`, `i18n/fr.json` per project, `tools/I18n keys / typo / check`),
French localised (ticket, tableau; notation keywords such as «include» stay as they are). Samples come in the user's
language.

## Verification
- Tests: `DevHub.Diagrams.Core.Tests` (model, operations, routing, layout), `DevHub.Diagrams.Database.Core.Tests`
  (MCD → MLD rules, SQL per dialect, the SQLite script run for real, import round trips, data dictionary, samples),
  `DevHub.Diagrams.Kinds.Tests` (UML parsing and rules, BPMN pool rules, mind map layout, mood board palette, strokes
  and assets, every sample builds and breaks no rule).
- Run in DevHub with `DEVHUB_PLUGIN_PATHS` and a scratch `DEVHUB_DATA_DIR`: every sample created and drawn (light and
  dark, English and French), Generate MLD, a mood board with a stored picture.
