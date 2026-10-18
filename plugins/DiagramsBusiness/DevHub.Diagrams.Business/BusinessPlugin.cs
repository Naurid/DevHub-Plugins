using DevHub.Diagrams;
using DevHub.Diagrams.Business;
using DevHub.Sdk.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.DiagramsBusinessPlugin))]

namespace DevHub.Plugins;

/// <summary>Flowcharts, BPMN business processes and mind maps for the Diagrams editor. Requires Diagrams.</summary>
public sealed class DiagramsBusinessPlugin : IPlugin
{
    public const string Id = "devhub.diagrams.business";

    public PluginInfo Info { get; } = new(Id, "Business Diagrams",
        "Flowcharts, BPMN business processes (pools, lanes, tasks, events, gateways, message flows) and mind maps.",
        [DiagramsPlugin.Id]) { Category = "Tools", Order = 613, Presets = ["Planning"] };

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<IDiagramKind, FlowchartKind>();
        services.AddSingleton<IDiagramKind, BpmnKind>();
        services.AddSingleton<IDiagramKind, MindMapKind>();
    }
}
