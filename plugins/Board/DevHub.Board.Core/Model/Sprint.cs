using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevHub.Board.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter<SprintState>))]
public enum SprintState
{
    Future,
    Active,
    Closed,
}

/// <summary>
/// <c>sprints/&lt;id&gt;.json</c>: a sprint. The data lives in Board.Core (search, reports and other plugins understand
/// sprints even when Board.Scrum is off); Board.Scrum adds the screens and the planning operations' UI.
/// </summary>
public sealed record Sprint
{
    public int Schema { get; init; } = BoardJson.SchemaVersion;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Goal { get; init; } = "";
    public SprintState State { get; init; } = SprintState.Future;

    /// <summary>Order among sprints (planning panels list future sprints by it).</summary>
    public int Sequence { get; init; }

    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }

    /// <summary>When it was completed (closed).</summary>
    public DateTimeOffset? Completed { get; init; }

    /// <summary>Planned capacity in story points; null = not set.</summary>
    public double? Capacity { get; init; }

    /// <summary>How many days each person can work in this sprint (holidays, part time, other projects).</summary>
    public IReadOnlyList<MemberDays> Availability { get; init; } = [];

    /// <summary>The share of an available day spent on sprint work (meetings and support take the rest); null = 1.</summary>
    public double? FocusFactor { get; init; }

    /// <summary>Snapshot taken when the sprint started: issue ids and their points (for "committed" in reports).</summary>
    public IReadOnlyList<SprintIssuePoints> Committed { get; init; } = [];

    /// <summary>Taken when it closed: the issues finished in it, with their points.</summary>
    public IReadOnlyList<SprintIssuePoints> CompletedIssues { get; init; } = [];

    /// <summary>Taken when it closed: the issues that weren't finished (and where they went).</summary>
    public IReadOnlyList<SprintIssuePoints> NotCompleted { get; init; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    [JsonIgnore] public double CommittedPoints => Committed.Sum(c => c.Points ?? 0);
    [JsonIgnore] public double CompletedPoints => CompletedIssues.Sum(c => c.Points ?? 0);

    /// <summary>Everyone's available days added up.</summary>
    [JsonIgnore] public double PersonDays => Availability.Sum(a => a.Days);

    /// <summary>Person-days times the focus factor: the days of real sprint work.</summary>
    [JsonIgnore] public double FocusedDays => PersonDays * (FocusFactor ?? 1);

    public override string ToString() => Name;
}

/// <summary>A person's available days in a sprint.</summary>
public sealed record MemberDays
{
    public string Person { get; init; } = "";
    public double Days { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>An issue and its story points at some moment of a sprint.</summary>
public sealed record SprintIssuePoints
{
    public string Issue { get; init; } = "";
    public double? Points { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary><c>filters/&lt;id&gt;.json</c>: a saved search, shared by everyone using the board.</summary>
public sealed record SavedFilter
{
    public int Schema { get; init; } = BoardJson.SchemaVersion;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Query { get; init; } = "";
    public string? Owner { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => Name;
}
