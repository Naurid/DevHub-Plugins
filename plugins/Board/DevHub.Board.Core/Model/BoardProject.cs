using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevHub.Board.Core.Model;

/// <summary>
/// <c>board.json</c>: the project a board store holds (one per store). Its key, the next issue number, and the
/// definitions every issue refers to by id: issue types, statuses, priorities, resolutions, link types, custom
/// fields, components and people. Workflows, boards and issues are files of their own.
/// </summary>
public sealed record BoardProject
{
    public int Schema { get; init; } = BoardJson.SchemaVersion;

    /// <summary>The key prefix of every issue, e.g. "DEV" for DEV-12.</summary>
    public string Key { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>The number the next issue gets.</summary>
    public int NextNumber { get; init; } = 1;

    public IReadOnlyList<IssueType> IssueTypes { get; init; } = [];
    public IReadOnlyList<Status> Statuses { get; init; } = [];
    public IReadOnlyList<Priority> Priorities { get; init; } = [];
    public IReadOnlyList<Resolution> Resolutions { get; init; } = [];
    public IReadOnlyList<LinkType> LinkTypes { get; init; } = [];
    public IReadOnlyList<FieldDefinition> CustomFields { get; init; } = [];
    public IReadOnlyList<Component> Components { get; init; } = [];
    public IReadOnlyList<BoardPerson> People { get; init; } = [];

    /// <summary>The priority new issues get; null = the middle one.</summary>
    public string? DefaultPriority { get; init; }

    /// <summary>The workflow of issue types without their own; null = the first workflow by id.</summary>
    public string? DefaultWorkflow { get; init; }

    public ProjectSettings Settings { get; init; } = new();

    /// <summary>Fields written by a newer DevHub: kept as they are when this file is rewritten.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public IssueType? IssueType(string? id) => IssueTypes.FirstOrDefault(t => t.Id == id);
    public Status? Status(string? id) => Statuses.FirstOrDefault(s => s.Id == id);
    public Priority? Priority(string? id) => Priorities.FirstOrDefault(p => p.Id == id);
    public Resolution? Resolution(string? id) => Resolutions.FirstOrDefault(r => r.Id == id);
    public LinkType? LinkType(string? id) => LinkTypes.FirstOrDefault(l => l.Id == id);
    public FieldDefinition? CustomField(string? id) => CustomFields.FirstOrDefault(f => f.Id == id);
    public Component? Component(string? id) => Components.FirstOrDefault(c => c.Id == id);
    public BoardPerson? Person(string? id) => People.FirstOrDefault(p => p.Id == id);

    /// <summary>The priority for a new issue: <see cref="DefaultPriority"/>, else the middle of the list.</summary>
    public string? DefaultPriorityId =>
        Priority(DefaultPriority)?.Id ?? (Priorities.Count == 0 ? null : Priorities[Priorities.Count / 2].Id);
}

/// <summary>Project-wide settings.</summary>
public sealed record ProjectSettings
{
    /// <summary>How many hours "1d" is in time tracking.</summary>
    public double HoursPerDay { get; init; } = 8;

    /// <summary>How many days "1w" is in time tracking.</summary>
    public double DaysPerWeek { get; init; } = 5;

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>Where an issue type sits in the hierarchy: epics above standard issues, sub-tasks below them.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IssueTypeLevel>))]
public enum IssueTypeLevel
{
    Subtask = -1,
    Standard = 0,
    Epic = 1,
}

/// <summary>An issue type (epic, story, task, bug, sub-task, or your own).</summary>
public sealed record IssueType
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Epic, standard or sub-task: decides which types may be an issue's parent.</summary>
    public IssueTypeLevel Level { get; init; } = IssueTypeLevel.Standard;

    /// <summary>A symbol the UI draws for it ("epic", "story", "task", "bug", "subtask", or any other name).</summary>
    public string Icon { get; init; } = "task";

    /// <summary>The workflow its issues follow; null = the project's first workflow.</summary>
    public string? Workflow { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => Name;
}

/// <summary>The three groups every status belongs to: they drive what counts as open, started and finished.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StatusCategory>))]
public enum StatusCategory
{
    ToDo,
    InProgress,
    Done,
}

/// <summary>A status an issue can be in ("To Do", "In Review", …).</summary>
public sealed record Status
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public StatusCategory Category { get; init; } = StatusCategory.ToDo;

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => Name;
}

/// <summary>A priority; the list's order is the ranking (first = highest).</summary>
public sealed record Priority
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>A symbol the UI draws ("highest", "high", "medium", "low", "lowest").</summary>
    public string Icon { get; init; } = "medium";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => Name;
}

/// <summary>Why a finished issue is finished ("Done", "Won't Do", "Duplicate", …).</summary>
public sealed record Resolution
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => Name;
}

/// <summary>A kind of link between two issues, named from both ends ("blocks" / "is blocked by").</summary>
public sealed record LinkType
{
    public string Id { get; init; } = "";

    /// <summary>Read from the issue that holds the link: "DEV-1 blocks DEV-2".</summary>
    public string Outward { get; init; } = "";

    /// <summary>Read from the other issue: "DEV-2 is blocked by DEV-1".</summary>
    public string Inward { get; init; } = "";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<FieldType>))]
public enum FieldType
{
    Text,
    Number,
    Date,
    Select,
    Person,
}

/// <summary>A custom field. Values are stored as text in <see cref="Issue.Custom"/>, read through its type.</summary>
public sealed record FieldDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public FieldType Type { get; init; } = FieldType.Text;

    /// <summary>The choices of a <see cref="FieldType.Select"/> field.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => Name;
}

/// <summary>A part of the product issues can be filed under ("UI", "Backend").</summary>
public sealed record Component
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => Name;
}

/// <summary>Someone issues can be assigned to or reported by.</summary>
public sealed record BoardPerson
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Email { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => Name;
}
