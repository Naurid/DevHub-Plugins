using DevHub.Sdk.Core.Plugins;
using DevHub.ViewModels.Documents;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.TemplatesPlugin))]

namespace DevHub.Plugins;

/// <summary>Templates: project and file templates (placeholder pages until milestone M1 builds the real thing).</summary>
public sealed class TemplatesPlugin : IPlugin
{
    public const string Id = "devhub.templates";

    // Requires Projects by id only: nothing of Projects is used yet, so no assembly reference.
    public PluginInfo Info { get; } = new(Id, "Templates",
        "Create new projects and files from templates with variables, or turn an existing folder into a template.",
        ["devhub.projects"]) { Category = "Templates", Order = 400 };

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<DocumentViewModelBase, TemplatesViewModel>();
        services.AddSingleton<DocumentViewModelBase, GenerateViewModel>();
        services.AddSingleton<DocumentViewModelBase, ImportViewModel>();
    }
}
