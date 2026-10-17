using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevHub.Board.Core.Model;

/// <summary>
/// One issue: <c>issues/&lt;id&gt;.json</c>. Everything it refers to (type, status, priority, people, versions,
/// components, other issues) is by id, so renaming any of them never touches issue files.
/// </summary>
public sealed record Issue
{
    public int Schema { get; init; } = BoardJson.SchemaVersion;

    /// <summary>Stable id: the file name, and what other files refer to. Never changes.</summary>
    public string Id { get; init; } = "";

    /// <summary>What people see and type ("DEV-12").</summary>
    public string Key { get; init; } = "";

    /// <summary>Earlier keys of this issue (after a renumbering), still recognised in branch names and commits.</summary>
    public IReadOnlyList<string> OldKeys { get; init; } = [];

    public string Type { get; init; } = "";
    public string Summary { get; init; } = "";

    /// <summary>Markdown.</summary>
    public string Description { get; init; } = "";

    public string Status { get; init; } = "";

    /// <summary>Set when the issue is in a Done status ("Done", "Won't Do"…), null otherwise.</summary>
    public string? Resolution { get; init; }

    public string? Priority { get; init; }

    /// <summary>The epic of a standard issue, or the issue of a sub-task.</summary>
    public string? Parent { get; init; }

    public string? Assignee { get; init; }
    public string? Reporter { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<string> Components { get; init; } = [];
    public IReadOnlyList<string> FixVersions { get; init; } = [];
    public double? StoryPoints { get; init; }
    public TimeTracking Time { get; init; } = new();
    /// <summary>When work is planned to start (the timeline draws epics from Start to Due).</summary>
    public DateOnly? Start { get; init; }

    public DateOnly? Due { get; init; }

    /// <summary>The sprint it's planned in (Board.Scrum), or null: in the backlog.</summary>
    public string? Sprint { get; init; }

    /// <summary>Order in the backlog and in columns (<see cref="Model.Rank"/>; compared ordinally).</summary>
    public string Rank { get; init; } = "";

    public IReadOnlyList<IssueLink> Links { get; init; } = [];

    /// <summary>Custom field id → value as text (read through the field's <see cref="FieldType"/>).</summary>
    public SortedDictionary<string, string> Custom { get; init; } = new(StringComparer.Ordinal);

    public IReadOnlyList<Comment> Comments { get; init; } = [];
    public IReadOnlyList<Attachment> Attachments { get; init; } = [];

    /// <summary>Acceptance criteria: what has to be true for the issue to be accepted, each ticked when it is.</summary>
    public IReadOnlyList<CheckItem> Criteria { get; init; } = [];

    /// <summary>The ids of the project's Definition of Done items ticked on this issue (<see cref="BoardProject.DefinitionOfDone"/>).</summary>
    public IReadOnlyList<string> DoneChecks { get; init; } = [];

    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; init; }

    /// <summary>When it reached a Done status (cleared when reopened); for reports.</summary>
    public DateTimeOffset? Resolved { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => $"{Key} {Summary}";
}

/// <summary>Time tracking, in minutes: the first estimate, what's left, and the work logged.</summary>
public sealed record TimeTracking
{
    public int? OriginalMinutes { get; init; }
    public int? RemainingMinutes { get; init; }
    public IReadOnlyList<WorkLog> Log { get; init; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    [JsonIgnore] public int SpentMinutes => Log.Sum(w => w.Minutes);
}

/// <summary>Work logged on an issue.</summary>
public sealed record WorkLog
{
    public string Id { get; init; } = "";
    public string? Author { get; init; }
    public DateTimeOffset Started { get; init; }
    public int Minutes { get; init; }
    public string Note { get; init; } = "";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>A link from this issue to another ("blocks DEV-7"). Stored on one side only.</summary>
public sealed record IssueLink
{
    public string Type { get; init; } = "";
    public string Target { get; init; } = "";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record Comment
{
    public string Id { get; init; } = "";
    public string? Author { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset? Edited { get; init; }

    /// <summary>Markdown.</summary>
    public string Body { get; init; } = "";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>A file attached to an issue; its bytes are <c>attachments/&lt;issue id&gt;/&lt;id&gt;-&lt;name&gt;</c>.</summary>
public sealed record Attachment
{
    public string Id { get; init; } = "";
    public string FileName { get; init; } = "";
    public long Size { get; init; }
    public string? Author { get; init; }
    public DateTimeOffset Added { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>A line with a check box: an acceptance criterion.</summary>
public sealed record CheckItem
{
    public string Id { get; init; } = "";
    public string Text { get; init; } = "";
    public bool Done { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => Text;
}
