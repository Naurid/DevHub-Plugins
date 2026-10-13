using DevHub.Sdk.Core.Settings;

namespace DevHub.Board.Reports.Core;

/// <summary>Board.Reports' settings: the gadgets of each board's dashboard.</summary>
public sealed class BoardReportsSettings : ISettingsSection
{
    public static string SectionId => "devhub.board.reports";

    /// <summary>Board location key → its gadgets, in order.</summary>
    public Dictionary<string, List<GadgetConfig>> Dashboards { get; set; } = new();
}

/// <summary>A dashboard gadget: what it shows and its one setting (a field for a pie, a query for results).</summary>
public sealed class GadgetConfig
{
    public string Kind { get; set; } = "assigned";
    public string Setting { get; set; } = "";
}

