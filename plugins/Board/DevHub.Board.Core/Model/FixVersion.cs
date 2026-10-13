using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevHub.Board.Core.Model;

/// <summary><c>versions/&lt;id&gt;.json</c>: a release issues can be planned for ("fix version").</summary>
public sealed record FixVersion
{
    public int Schema { get; init; } = BoardJson.SchemaVersion;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public DateOnly? Start { get; init; }
    public DateOnly? Release { get; init; }
    public bool Released { get; init; }
    public bool Archived { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// One line of <c>history/&lt;issue id&gt;.jsonl</c>: a field of the issue changed. Values are stored raw (ids,
/// invariant numbers, minutes), so the UI shows today's names for them; null = empty.
/// </summary>
public sealed record HistoryEntry
{
    public DateTimeOffset At { get; init; }
    public string? Who { get; init; }

    /// <summary>The field (<see cref="Fields.IssueFields"/>), or an event: "created", "comment", "worklog", "link", "attachment".</summary>
    public string Field { get; init; } = "";

    public string? From { get; init; }
    public string? To { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
