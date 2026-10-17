using DevHub.Integration;
using DevHub.Sdk.Core.Plugins;
using DevHub.ViewModels.Charts;
using DevHub.ViewModels.Documents;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.ChartsPlugin))]

namespace DevHub.Plugins;

/// <summary>Charts: the chart maker tab (your own data in every chart type), and IChartMaker for other plugins' charts.</summary>
public sealed class ChartsPlugin : IPlugin
{
    public const string Id = "devhub.charts";

    public PluginInfo Info { get; } = new(Id, "Charts",
        "A chart maker: paste a table (or open a CSV), pick a chart type and style, and save it as an image for your documents.", [])
        { Category = "Tools", Order = 600, Presets = ["Planning"] };

    public IEnumerable<Uri> Resources => [new("avares://DevHub.Charts/Themes/ChartsTokens.axaml")];

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<ChartMakerViewModel>();
        services.AddSingleton<DocumentViewModelBase>(sp => sp.GetRequiredService<ChartMakerViewModel>());
        services.AddSingleton<IChartMaker, ChartMaker>();
    }
}
