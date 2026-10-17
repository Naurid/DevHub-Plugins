using DevHub.Sdk.Core.Settings;

namespace DevHub.Board.Reports.Core;

/// <summary>Board.Reports' settings: each board's dashboard gadgets, the chart type chosen per report, forecast inputs.</summary>
public sealed class BoardReportsSettings : ISettingsSection
{
    public static string SectionId => "devhub.board.reports";

    /// <summary>Board location key → its gadgets, in order.</summary>
    public Dictionary<string, List<GadgetConfig>> Dashboards { get; set; } = new();

    /// <summary>Report id → the chart type picked for it (a <c>ChartKind</c> name), for every board.</summary>
    public Dictionary<string, string> ChartKinds { get; set; } = new();

    /// <summary>Board location key → the forecast's scope and velocities last used.</summary>
    public Dictionary<string, ForecastSettings> Forecasts { get; set; } = new();
}

/// <summary>A dashboard gadget: what it shows and its one setting (a field for a pie, a query for results).</summary>
public sealed class GadgetConfig
{
    public string Kind { get; set; } = "assigned";
    public string Setting { get; set; } = "";
}

/// <summary>A board's forecast inputs: the scope ("backlog", "version:…", "epic:…", "filter:…") and velocities as typed.</summary>
public sealed class ForecastSettings
{
    public string Scope { get; set; } = "backlog";
    public string Pessimistic { get; set; } = "";
    public string Neutral { get; set; } = "";
    public string Optimistic { get; set; } = "";

    /// <summary>Planned velocity per coming sprint ("5, 8, 12"), for a team that's still forming; empty = the neutral one.</summary>
    public string Planned { get; set; } = "";
}
