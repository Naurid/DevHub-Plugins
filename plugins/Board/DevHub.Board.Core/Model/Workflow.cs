using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevHub.Board.Core.Model;

/// <summary>
/// <c>workflows/&lt;id&gt;.json</c>: which statuses issues of a type move through and which moves are allowed. The
/// statuses themselves (name, category) are defined once in <c>board.json</c>; a workflow lists them in order.
/// </summary>
public sealed record Workflow
{
    public int Schema { get; init; } = BoardJson.SchemaVersion;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Status ids, in order; the first is where new issues start.</summary>
    public IReadOnlyList<string> Statuses { get; init; } = [];

    /// <summary>True: any status may move to any other (the default). False: only <see cref="Transitions"/>.</summary>
    public bool AnyToAny { get; init; } = true;

    public IReadOnlyList<WorkflowTransition> Transitions { get; init; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>Where new issues start.</summary>
    [JsonIgnore] public string? InitialStatus => Statuses.Count > 0 ? Statuses[0] : null;

    /// <summary>Whether an issue in <paramref name="from"/> may move to <paramref name="to"/>.</summary>
    public bool Allows(string from, string to) =>
        from != to && Statuses.Contains(to)
        && (AnyToAny || Transitions.Any(t => (t.From is null || t.From == from) && t.To == to));

    public override string ToString() => Name;
}

/// <summary>An allowed move. <see cref="From"/> null = from any status ("global transition").</summary>
public sealed record WorkflowTransition
{
    public string? From { get; init; }
    public string To { get; init; } = "";

    /// <summary>A label for the move ("Start progress"); null = the target status's name.</summary>
    public string? Name { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
