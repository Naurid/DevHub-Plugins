using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevHub.Board.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter<BoardKind>))]
public enum BoardKind
{
    /// <summary>Every issue the board's filter matches.</summary>
    Kanban,

    /// <summary>Only the active sprint's issues (Board.Scrum); the rest is planned in the backlog.</summary>
    Scrum,
}

[JsonConverter(typeof(JsonStringEnumConverter<SwimlaneMode>))]
public enum SwimlaneMode
{
    None,
    Assignee,
    Epic,
    Priority,
    Type,
}

/// <summary>
/// <c>boards/&lt;id&gt;.json</c>: one view onto the project's issues. Its own columns (each holding one or more
/// statuses), filter, swimlanes, quick filters and card layout. A project has any number of boards.
/// </summary>
public sealed record BoardDefinition
{
    public int Schema { get; init; } = BoardJson.SchemaVersion;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public BoardKind Kind { get; init; } = BoardKind.Kanban;
    public IReadOnlyList<BoardColumn> Columns { get; init; } = [];

    /// <summary>Which issues the board shows (a search query, B6); empty = all of them.</summary>
    public string Filter { get; init; } = "";

    /// <summary>Hide sub-tasks as cards (they show on their parent's card instead).</summary>
    public bool HideSubtasks { get; init; }

    /// <summary>Hide epics as cards (they're the epics panel and the card's epic label instead).</summary>
    public bool HideEpics { get; init; } = true;

    /// <summary>Done issues older than this many days leave the board (Kanban); null = keep them.</summary>
    public int? HideDoneAfterDays { get; init; } = 14;

    public SwimlaneMode Swimlanes { get; init; } = SwimlaneMode.None;
    public IReadOnlyList<QuickFilter> QuickFilters { get; init; } = [];

    /// <summary>The fields shown on a card besides key, summary and type ("epic", "storyPoints", "assignee", …).</summary>
    public IReadOnlyList<string> CardFields { get; init; } = ["epic", "storyPoints", "priority", "assignee"];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>The column a status is on, or null: "not on this board".</summary>
    public BoardColumn? ColumnOf(string statusId) => Columns.FirstOrDefault(c => c.Statuses.Contains(statusId));

    public override string ToString() => Name;
}

/// <summary>A board column: a name, the statuses it holds (cards in any of them show here) and a WIP limit.</summary>
public sealed record BoardColumn
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Statuses { get; init; } = [];

    /// <summary>Most cards it should hold (the header turns red above it); null = no limit.</summary>
    public int? WipLimit { get; init; }

    /// <summary>Fewest cards it should hold; null = no minimum.</summary>
    public int? WipMinimum { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => Name;
}

/// <summary>A one-click filter above the board ("Only my issues"); <see cref="Query"/> is a search query (B6).</summary>
public sealed record QuickFilter
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Query { get; init; } = "";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => Name;
}
