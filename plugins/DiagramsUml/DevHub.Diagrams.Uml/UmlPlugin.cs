using DevHub.Diagrams;
using DevHub.Diagrams.Uml;
using DevHub.Diagrams.Uml.Core;
using DevHub.Sdk.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.DiagramsUmlPlugin))]

namespace DevHub.Plugins;

/// <summary>
/// UML diagrams for the Diagrams editor: use case, activity, class, object, sequence, state machine, component,
/// deployment and package diagrams, with the notation's rules and PlantUML / Mermaid export. Requires Diagrams.
/// </summary>
public sealed class DiagramsUmlPlugin : IPlugin
{
    public const string Id = "devhub.diagrams.uml";

    public PluginInfo Info { get; } = new(Id, "UML Diagrams",
        "Use case, activity, class, object, sequence, state machine, component, deployment and package diagrams, checked against the UML notation and exported as PlantUML or Mermaid.",
        [DiagramsPlugin.Id]) { Category = "Tools", Order = 611, Presets = ["Planning"] };

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<IDiagramKind, UseCaseKind>();
        services.AddSingleton<IDiagramKind, ActivityKind>();
        services.AddSingleton<IDiagramKind>(new ClassKind());
        services.AddSingleton<IDiagramKind>(new ClassKind(objects: true));
        services.AddSingleton<IDiagramKind, SequenceKind>();
        services.AddSingleton<IDiagramKind, StateKind>();
        services.AddSingleton<IDiagramKind>(new StructureKind(StructureModel.ComponentKind));
        services.AddSingleton<IDiagramKind>(new StructureKind(StructureModel.DeploymentKind));
        services.AddSingleton<IDiagramKind>(new StructureKind(StructureModel.PackageKind));
    }
}
