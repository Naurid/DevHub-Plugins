using DevHub.Actions;
using DevHub.Actions.Providers;
using DevHub.Integration;
using DevHub.Sdk.Core.Plugins;
using DevHub.Services;
using DevHub.ViewModels.Documents;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.TemplatesPlugin))]

namespace DevHub.Plugins;

/// <summary>Templates: project and file templates (New Project now; the editor, item templates and import follow).</summary>
public sealed class TemplatesPlugin : IPlugin
{
    public const string Id = "devhub.templates";

    // Requires Projects by id only: it uses Projects' folders through DevHub.Integration (IProjectFolders),
    // so no assembly reference.
    public PluginInfo Info { get; } = new(Id, "Templates",
        "Create new projects and files from templates with variables, or turn an existing folder into a template.",
        ["devhub.projects"]) { Category = "Templates", Order = 400, Presets = ["Project templates"] };

    public IEnumerable<Uri> Resources => [new("avares://DevHub.Templates/Themes/TemplatesTokens.axaml")];

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<TemplatesService>();
        // Asked for when needed, not injected into New Project: Git's opener needs the DocumentManager, which
        // is built from the documents (New Project too), and the Projects tool window takes that opener, so
        // injecting either directly is a cycle (startup hangs without a window).
        services.AddSingleton<Func<IProjectOpener?>>(sp => () => sp.GetServices<IProjectOpener>().FirstOrDefault());
        services.AddSingleton<Func<IProjectFolders?>>(sp => () => sp.GetServices<IProjectFolders>().FirstOrDefault());
        services.AddSingleton<Func<IProjectFileEditor?>>(sp => () => sp.GetServices<IProjectFileEditor>().FirstOrDefault());

        services.AddSingleton<GenerateViewModel>();
        services.AddSingleton<DocumentViewModelBase>(sp => sp.GetRequiredService<GenerateViewModel>());
        services.AddSingleton<AddFilesViewModel>();
        services.AddSingleton<DocumentViewModelBase>(sp => sp.GetRequiredService<AddFilesViewModel>());
        services.AddSingleton<IAddFilesFromTemplate, AddFilesOpener>(); // Projects and Git show "Add Files from Template…" with it
        services.AddSingleton<TemplatesViewModel>();
        services.AddSingleton<DocumentViewModelBase>(sp => sp.GetRequiredService<TemplatesViewModel>());
        services.AddSingleton<IDocumentFactory, TemplateEditorFactory>();
        services.AddSingleton<DocumentViewModelBase, ImportViewModel>();

        services.AddSingleton<IActionProvider, TemplatesActionProvider>();
    }
}
