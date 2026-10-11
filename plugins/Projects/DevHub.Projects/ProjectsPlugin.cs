using DevHub.Actions;
using DevHub.Actions.Providers;
using DevHub.Integration;
using DevHub.Projects.Core;
using DevHub.Sdk.Core.Plugins;
using DevHub.Services;
using DevHub.ViewModels.Dialogs.Settings;
using DevHub.ViewModels.ToolWindows;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.ProjectsPlugin))]

namespace DevHub.Plugins;

/// <summary>Projects: folders to scan, the repositories in them, their languages, Open With.</summary>
public sealed class ProjectsPlugin : IPlugin
{
    public const string Id = "devhub.projects";

    public PluginInfo Info { get; } = new(Id, "Projects",
        "The Projects tool window: choose folders, find the git repositories in them, see their language and open them in the right tool.",
        []) { Category = "Projects", Order = 100 };

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<IToolLocator, ToolLocator>();
        services.AddSingleton<IProjectLanguageDetector, ProjectLanguageDetector>();
        services.AddSingleton<IProjectLauncher, ProjectLauncher>();
        services.AddSingleton<IRepositoryScanner, RepositoryScanner>();
        // Offered to other plugins without depending on them (DevHub.Integration): Git uses these
        // when Projects is on (Open in Editor with Open With, Clone into a project folder).
        services.AddSingleton<IProjectFileEditor, ProjectFileEditor>();

        services.AddSingleton<RepositoriesViewModel>();
        services.AddSingleton<ToolWindowViewModelBase>(sp => sp.GetRequiredService<RepositoriesViewModel>());
        services.AddSingleton<IProjectFolders>(sp => sp.GetRequiredService<RepositoriesViewModel>());
        services.AddSingleton<IActionProvider, RepositoryActionProvider>();
        services.AddTransient<SettingsPageViewModelBase, OpenWithSettingsPageViewModel>();
    }
}
