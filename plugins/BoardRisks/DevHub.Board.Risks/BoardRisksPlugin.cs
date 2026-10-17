using DevHub.Board;
using DevHub.Sdk.Core.Plugins;
using DevHub.ViewModels.Risks;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.BoardRisksPlugin))]

namespace DevHub.Plugins;

/// <summary>Board.Risks: a Risks page on every board (register, probability × impact matrix, plans). Requires Board.</summary>
public sealed class BoardRisksPlugin : IPlugin
{
    public const string Id = "devhub.board.risks";

    public PluginInfo Info { get; } = new(Id, "Board Risks",
        "A risk register for each board: probability and impact, a probability × impact matrix, owners, mitigation and contingency plans.",
        [BoardPlugin.Id]) { Category = "Planning", Order = 550, Presets = ["Planning"] };

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<IBoardPageProvider, RisksPageProvider>();
    }
}
