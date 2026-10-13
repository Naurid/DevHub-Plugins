using DevHub.Board;
using DevHub.Sdk.Core.Plugins;
using DevHub.ViewModels.Timeline;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.BoardTimelinePlugin))]

namespace DevHub.Plugins;

/// <summary>Board.Timeline: the roadmap of epics and the releases page. Requires Board.</summary>
public sealed class BoardTimelinePlugin : IPlugin
{
    public const string Id = "devhub.board.timeline";

    public PluginInfo Info { get; } = new(Id, "Board Timeline",
        "A roadmap of epics with their dates, progress and blockers, and releases (fix versions) with their progress and release notes.",
        [BoardPlugin.Id]) { Category = "Planning", Order = 540, Presets = ["Planning"] };

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<IBoardPageProvider, TimelinePageProvider>();
        services.AddSingleton<IBoardPageProvider, ReleasesPageProvider>();
    }
}
