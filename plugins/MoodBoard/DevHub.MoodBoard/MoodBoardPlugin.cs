using DevHub.Diagrams;
using DevHub.MoodBoard;
using DevHub.Sdk.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.MoodBoardPlugin))]

namespace DevHub.Plugins;

/// <summary>Mood boards on the Diagrams canvas. Requires Diagrams.</summary>
public sealed class MoodBoardPlugin : IPlugin
{
    public const string Id = "devhub.moodboard";

    public PluginInfo Info { get; } = new(Id, "Mood Boards",
        "Mood boards in the Diagrams editor: pictures (dropped, pasted, picked), colour swatches and palettes from pictures, shapes and frames, links, text and freehand drawing.",
        [DiagramsPlugin.Id]) { Category = "Tools", Order = 614, Presets = ["Planning"] };

    public void ConfigureServices(IServiceCollection services, PluginContext context) =>
        services.AddSingleton<IDiagramKind, MoodBoardKind>();
}
