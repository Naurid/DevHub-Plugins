# Board demo

Writes a demo board to try every Board feature with: about seven weeks of a small team building a web shop. The
weeks are replayed through the same operations the Board UI uses, so the change log, the reports and each issue's
history are real. It also writes a git repository whose branches and commits name the issues, for Board.Git's
Development section. Dates are relative to the day you run it: the fourth sprint is always three days in.

```powershell
dotnet run --project tools/BoardDemo -- C:\Temp\ShopDemo                  # board + repository
dotnet run --project tools/BoardDemo -- C:\Temp\ShopDemo --me "Your Name" # your name on boards (default: the Windows user)
dotnet run --project tools/BoardDemo -- C:\Temp\ShopDemo --no-git         # only the board
```

The folder must be new or empty. In DevHub, use **Open Board…** and pick `C:\Temp\ShopDemo\board`. Your name in
Settings → Board must match `--me`, or "Only my issues" and "My open work" won't find your issues.

## What's in it

| Where | What to look at |
|---|---|
| Scrum board | Sprint 4 running: **In Progress over its WIP limit** (4); QA column; the "Bugs", "High priority" and "Web" quick filters; labels and due dates on the cards |
| Team Kanban | swimlanes per assignee; remaining estimates on the cards |
| Bug triage | bugs only, with a Triage column (the bug workflow), swimlanes per priority |
| Backlog | Sprint 4 (active), Sprints 5 and 6 planned (Sprint 5 has a capacity of 22), the backlog below; the epics panel |
| Issues | 59: 6 epics, stories, tasks, a custom **Spike** type, bugs, sub-tasks; descriptions, comments, attachments (`crash-log.txt` on SHOP-31, a wireframe on SHOP-43), work logs, links (blocks, relates, duplicates), custom fields (Platform, Business value, Customer); the **History** tab |
| Search | the saved filters: My open work, Open bugs, Release 1.1, Stories without points, Overdue |
| Reports (Board.Reports) | burndown per sprint (a story carried over in Sprint 1, one taken out in Sprint 2, an estimate raised in Sprint 3), velocity of 3 sprints, cumulative flow, control chart, epic progress |
| Timeline (Board.Timeline) | the roadmap: Product catalogue **late**, Payments **conflicts** with Cart and checkout (its blocker), Order history's dates guessed from its stories; Releases: 1.0 released, 1.1 and 2.0 to come, release notes |
| Development (Board.Git) | in an issue: its branches and commits, e.g. SHOP-44 (merged branch) or SHOP-42 (still open) |
| Settings | Project Settings: 6 people, 5 components, 2 workflows (bugs start in Triage), custom fields, the Spike type |
