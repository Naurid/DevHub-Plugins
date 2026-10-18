using CommunityToolkit.Mvvm.Input;
using DevHub.Actions;
using DevHub.Controls;
using DevHub.Diagrams;
using DevHub.Integration;
using DevHub.Sdk.Core.Localization;
using DevHub.Sdk.Core.Plugins;
using DevHub.ViewModels.Documents;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.DiagramsPlugin))]

namespace DevHub.Plugins;

/// <summary>
/// Diagrams: the editor tab for <c>*.devhub-diagram.json</c> files, New / Open Diagram, image and text export, and the
/// <see cref="IProjectDiagrams"/> hook (Projects lists a project's diagrams, Board an issue's). The kinds of diagram
/// come from other plugins through <see cref="IDiagramKind"/>.
/// </summary>
public sealed class DiagramsPlugin : IPlugin
{
    public const string Id = "devhub.diagrams";

    public PluginInfo Info { get; } = new(Id, "Diagrams",
        "A diagram editor: draw, check and export diagrams (PNG, SVG, text), linked to your projects and boards. The kinds come with the Diagrams plugins (UML, databases, business processes, mood boards).", [])
        { Category = "Tools", Order = 610, Presets = ["Planning"] };

    public IEnumerable<Uri> Resources => [new("avares://DevHub.Diagrams/Themes/DiagramsTokens.axaml")];

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<DiagramsService>();
        services.AddSingleton<IProjectDiagrams>(sp => sp.GetRequiredService<DiagramsService>());
        services.AddSingleton<IDocumentFactory, DiagramDocumentFactory>();
        services.AddSingleton<IActionProvider, DiagramActionProvider>();
    }
}

/// <summary>New Diagram… and Open Diagram… (File menu, Welcome, Find Action).</summary>
public sealed class DiagramActionProvider(IServiceProvider services) : IActionProvider
{
    // The service is resolved on use: it takes the document service, which is built from the actions.
    private DiagramsService Service => services.GetRequiredService<DiagramsService>();

    public IEnumerable<AppAction> GetActions()
    {
        yield return new AppAction("Diagrams.New", L.T("New Diagram…"), new AsyncRelayCommand(() => Service.CreateAsync(null, null)))
        {
            Category = "Diagrams",
            IconKind = IconKind.Add,
            Menu = "File",
            MenuOrder = 62,
            WelcomeOrder = 72,
            Description = L.T("A use case, activity or class diagram, a database model (Merise) or a mood board, in the project in front."),
        };
        yield return new AppAction("Diagrams.Open", L.T("Open Diagram…"), new AsyncRelayCommand(() => Service.OpenFromDiskAsync()))
        {
            Category = "Diagrams",
            IconKind = IconKind.Folder,
            Menu = "File",
            MenuOrder = 63,
            Description = L.T("Open a diagram or mood board file (.devhub-diagram.json)."),
        };
    }
}
