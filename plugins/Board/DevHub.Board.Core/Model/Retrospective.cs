using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevHub.Board.Core.Model;

/// <summary>
/// <c>retros/&lt;sprint id&gt;.json</c>: what the team said at a sprint's retrospective. Findings (what went well, what to
/// improve), the problems behind them, the improvement actions decided (each with an owner, maybe an issue) and how
/// they'll be followed up. Actions not done yet show again at the next retrospective.
/// </summary>
public sealed record Retrospective
{
    public int Schema { get; init; } = BoardJson.SchemaVersion;

    /// <summary>The sprint's id (also the file name).</summary>
    public string Sprint { get; init; } = "";

    public IReadOnlyList<RetroNote> WentWell { get; init; } = [];
    public IReadOnlyList<RetroNote> ToImprove { get; init; } = [];
    public IReadOnlyList<RetroNote> Problems { get; init; } = [];
    public IReadOnlyList<RetroAction> Actions { get; init; } = [];

    /// <summary>How the actions are followed up (Markdown): who checks, when, what counts as better.</summary>
    public string FollowUp { get; init; } = "";

    public DateTimeOffset Updated { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>One sticky note.</summary>
public sealed record RetroNote
{
    public string Id { get; init; } = "";
    public string Text { get; init; } = "";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => Text;
}

/// <summary>An improvement the team decided on.</summary>
public sealed record RetroAction
{
    public string Id { get; init; } = "";
    public string Text { get; init; } = "";
    public string? Owner { get; init; }
    public bool Done { get; init; }

    /// <summary>The issue that tracks it, if one was created.</summary>
    public string? Issue { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => Text;
}
