using DevHub.Board;
using DevHub.Sdk.Core.Plugins;
using DevHub.ViewModels.Reports;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.BoardReportsPlugin))]

namespace DevHub.Plugins;

/// <summary>Board.Reports: the Reports page (charts, sprint report, epic progress, dashboard). Requires Board.</summary>
public sealed class BoardReportsPlugin : IPlugin
{
    public const string Id = "devhub.board.reports";

    public PluginInfo Info { get; } = new(Id, "Board Reports",
        "Reports for boards: sprint burndown and burnup, sprint report, velocity, cumulative flow, control chart, epic progress, and a dashboard of gadgets.",
        [BoardPlugin.Id]) { Category = "Planning", Order = 530, Presets = ["Planning"] };

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<IBoardPageProvider, ReportsPageProvider>();
    }
}
