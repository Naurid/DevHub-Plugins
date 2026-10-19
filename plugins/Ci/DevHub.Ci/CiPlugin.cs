using DevHub.Actions;
using DevHub.Integration;
using DevHub.Sdk.Core.Plugins;
using DevHub.Services.Ci;
using DevHub.ViewModels.Ci;
using DevHub.ViewModels.Documents;
using DevHub.ViewModels.Shell;
using DevHub.ViewModels.ToolWindows;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.CiPlugin))]

namespace DevHub.Plugins;

/// <summary>
/// CI: the GitHub Actions runs of the repositories open as tabs, in a bottom tool window, a status-bar mark and marks
/// next to commits and branches in Git; the failed step's log in a tab; re-runs. Requires GitHub (its IGitHubApi
/// makes the calls as the signed-in user).
/// </summary>
public sealed class CiPlugin : IPlugin
{
    public const string Id = "devhub.ci";

    public PluginInfo Info { get; } = new(Id, "CI",
        "GitHub Actions for your open repositories: run history, a mark when a run passes or fails, the failed step's log, re-run a job.",
        ["devhub.github"]) { Category = "Version control", Order = 320, Presets = ["Git client"] };

    public IEnumerable<Uri> Resources => [new("avares://DevHub.Ci/Themes/CiTokens.axaml")];

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<CiService>();
        // Offered to Git (DevHub.Integration): ✓ ✗ ● next to commits and branches.
        services.AddSingleton<IGitDecorator>(sp => sp.GetRequiredService<CiService>());
        services.AddSingleton<CiToolWindowViewModel>();
        services.AddSingleton<ToolWindowViewModelBase>(sp => sp.GetRequiredService<CiToolWindowViewModel>());
        services.AddSingleton<StatusBarWidgetViewModelBase, CiStatusWidgetViewModel>();
        services.AddSingleton<IDocumentFactory, CiLogDocumentFactory>();
        services.AddSingleton<IActionProvider, CiActionProvider>();
    }
}
