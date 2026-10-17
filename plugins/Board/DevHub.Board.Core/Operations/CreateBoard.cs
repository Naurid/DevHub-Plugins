using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;

namespace DevHub.Board.Core;

/// <summary>What a new board starts with. Only a starting point: everything can be changed afterwards.</summary>
public enum BoardPreset
{
    /// <summary>To Do, In Progress, In Review, Done; a Scrum board (sprints with Board.Scrum).</summary>
    Scrum,

    /// <summary>Backlog, Selected for Development, In Progress, Done; a Kanban board.</summary>
    Kanban,

    /// <summary>New, Triaged, In Progress, Fixed, Won't Fix; a Kanban board for bugs.</summary>
    BugTriage,

    /// <summary>To Do and Done statuses, a board without columns: build your own.</summary>
    Empty,
}

/// <summary>Someone using DevHub, as they appear on boards.</summary>
public sealed record BoardUser(string Name, string? Email = null)
{
    /// <summary>The person on <paramref name="project"/> who is this user (same email, else same name), or null.</summary>
    public BoardPerson? FindIn(BoardProject project) =>
        project.People.FirstOrDefault(p => Email is not null && string.Equals(p.Email, Email, StringComparison.OrdinalIgnoreCase))
        ?? project.People.FirstOrDefault(p => string.Equals(p.Name, Name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Creates a board in an empty store: board.json, one workflow and one board from <see cref="Preset"/>.</summary>
public sealed record CreateBoard(string Key, string Name, BoardPreset Preset, BoardUser Creator, string? CreatorId = null)
{
    /// <summary>The files of the new board (not an <see cref="IBoardOperation"/>: there's no snapshot yet).</summary>
    public BoardChange Build(OperationContext context)
    {
        var key = Key.Trim().ToUpperInvariant();
        if (!IssueKey.IsValidProjectKey(key))
            throw new BoardRuleException("A project key is 1–10 capital letters or digits, starting with a letter (like DEV).");
        var name = string.IsNullOrWhiteSpace(Name) ? key : Name.Trim();
        if (string.IsNullOrWhiteSpace(Creator.Name))
            throw new BoardRuleException("Your name is needed for the board.");

        var (statuses, kind, columns) = BoardPresets.Statuses(Preset);
        var workflowId = context.NewId();
        var project = BoardPresets.Defaults with
        {
            Key = key,
            Name = name,
            Statuses = statuses,
            People = [new BoardPerson { Id = BoardIds.IsValid(CreatorId) ? CreatorId! : context.NewId(), Name = Creator.Name.Trim(), Email = string.IsNullOrWhiteSpace(Creator.Email) ? null : Creator.Email.Trim() }],
            DefaultWorkflow = workflowId,
        };
        if (Preset == BoardPreset.Scrum)
            project = project with { CustomFields = BoardPresets.ScrumFields, DefinitionOfDone = BoardPresets.ScrumDefinitionOfDone };

        var workflow = new Workflow { Id = workflowId, Name = "Default", Statuses = statuses.Select(s => s.Id).ToList() };
        var board = new BoardDefinition
        {
            Id = context.NewId(),
            Name = Preset switch
            {
                BoardPreset.Scrum => $"{key} Scrum board",
                BoardPreset.BugTriage => "Bug triage",
                _ => $"{key} board",
            },
            Kind = kind,
            Columns = columns.Select(c => new BoardColumn { Id = context.NewId(), Name = c.Name, Statuses = c.Statuses }).ToList(),
            Filter = Preset == BoardPreset.BugTriage ? "type = bug" : "",
            QuickFilters =
            [
                new QuickFilter { Id = context.NewId(), Name = "Only my issues", Query = "assignee = currentUser()" },
                new QuickFilter { Id = context.NewId(), Name = "Recently updated", Query = "updated >= -1d" },
            ],
        };

        var files = new Dictionary<string, byte[]?>(StringComparer.Ordinal)
        {
            [BoardPaths.Project] = BoardJson.Serialize(project),
            [BoardPaths.Workflow(workflow.Id)] = BoardJson.Serialize(workflow),
            [BoardPaths.Board(board.Id)] = BoardJson.Serialize(board),
        };
        return new BoardChange($"Board {key} created", files);
    }
}

/// <summary>The definitions a new project starts with.</summary>
public static class BoardPresets
{
    /// <summary>Issue types, priorities, resolutions and link types every new board gets (statuses come from the preset).</summary>
    public static BoardProject Defaults { get; } = new()
    {
        IssueTypes =
        [
            new IssueType { Id = "epic", Name = "Epic", Level = IssueTypeLevel.Epic, Icon = "epic" },
            new IssueType { Id = "story", Name = "Story", Icon = "story" },
            new IssueType { Id = "task", Name = "Task", Icon = "task" },
            new IssueType { Id = "bug", Name = "Bug", Icon = "bug" },
            new IssueType { Id = "subtask", Name = "Sub-task", Level = IssueTypeLevel.Subtask, Icon = "subtask" },
        ],
        Priorities =
        [
            new Priority { Id = "highest", Name = "Highest", Icon = "highest" },
            new Priority { Id = "high", Name = "High", Icon = "high" },
            new Priority { Id = "medium", Name = "Medium", Icon = "medium" },
            new Priority { Id = "low", Name = "Low", Icon = "low" },
            new Priority { Id = "lowest", Name = "Lowest", Icon = "lowest" },
        ],
        DefaultPriority = "medium",
        Resolutions =
        [
            new Resolution { Id = "done", Name = "Done" },
            new Resolution { Id = "wontdo", Name = "Won't Do" },
            new Resolution { Id = "duplicate", Name = "Duplicate" },
            new Resolution { Id = "cannotreproduce", Name = "Cannot Reproduce" },
        ],
        LinkTypes =
        [
            new LinkType { Id = "blocks", Outward = "blocks", Inward = "is blocked by" },
            new LinkType { Id = "relates", Outward = "relates to", Inward = "relates to" },
            new LinkType { Id = "duplicates", Outward = "duplicates", Inward = "is duplicated by" },
            new LinkType { Id = "clones", Outward = "clones", Inward = "is cloned by" },
        ],
    };

    /// <summary>A Scrum board's custom fields: Business value, to prioritise the backlog by.</summary>
    public static IReadOnlyList<FieldDefinition> ScrumFields { get; } =
    [
        new FieldDefinition { Id = "value", Name = "Business value", Type = FieldType.Number },
    ];

    /// <summary>A Scrum board's starting Definition of Done (edit it in Project Settings).</summary>
    public static IReadOnlyList<DoneItem> ScrumDefinitionOfDone { get; } =
    [
        new DoneItem { Id = "standards", Text = "Code follows the team's standards" },
        new DoneItem { Id = "tests", Text = "Unit tests written and passing" },
        new DoneItem { Id = "review", Text = "Code reviewed" },
        new DoneItem { Id = "docs", Text = "Documentation updated" },
        new DoneItem { Id = "deployed", Text = "Deployed to the test environment" },
        new DoneItem { Id = "accepted", Text = "Accepted by the Product Owner" },
    ];

    /// <summary>A preset's statuses, board kind and columns (column name + the status ids it holds).</summary>
    public static (IReadOnlyList<Status> Statuses, BoardKind Kind, IReadOnlyList<(string Name, IReadOnlyList<string> Statuses)> Columns)
        Statuses(BoardPreset preset)
    {
        IReadOnlyList<Status> statuses = preset switch
        {
            BoardPreset.Scrum =>
            [
                S("todo", "To Do", StatusCategory.ToDo), S("inprogress", "In Progress", StatusCategory.InProgress),
                S("inreview", "In Review", StatusCategory.InProgress), S("done", "Done", StatusCategory.Done),
            ],
            BoardPreset.Kanban =>
            [
                S("backlog", "Backlog", StatusCategory.ToDo), S("selected", "Selected for Development", StatusCategory.ToDo),
                S("inprogress", "In Progress", StatusCategory.InProgress), S("done", "Done", StatusCategory.Done),
            ],
            BoardPreset.BugTriage =>
            [
                S("new", "New", StatusCategory.ToDo), S("triaged", "Triaged", StatusCategory.ToDo),
                S("inprogress", "In Progress", StatusCategory.InProgress), S("fixed", "Fixed", StatusCategory.Done),
                S("wontfix", "Won't Fix", StatusCategory.Done),
            ],
            _ => [S("todo", "To Do", StatusCategory.ToDo), S("done", "Done", StatusCategory.Done)],
        };
        var kind = preset == BoardPreset.Scrum ? BoardKind.Scrum : BoardKind.Kanban;
        IReadOnlyList<(string, IReadOnlyList<string>)> columns = preset == BoardPreset.Empty
            ? []
            : statuses.Select(s => (s.Name, (IReadOnlyList<string>)[s.Id])).ToList();
        return (statuses, kind, columns);

        static Status S(string id, string name, StatusCategory category) => new() { Id = id, Name = name, Category = category };
    }
}
