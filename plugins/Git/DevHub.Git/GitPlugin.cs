using DevHub.Actions;
using DevHub.Actions.Providers;
using DevHub.Git.Core;
using DevHub.Integration;
using DevHub.Sdk.Core.Plugins;
using DevHub.Sdk.Core.Settings;
using DevHub.Services;
using DevHub.ViewModels.Documents;
using DevHub.ViewModels.Git;
using DevHub.ViewModels.Shell;
using DevHub.ViewModels.ToolWindows;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.GitPlugin))]

namespace DevHub.Plugins;

/// <summary>Git: the Fork-style Git client (repository, diff and blame tabs, Git Console, branch widget).</summary>
public sealed class GitPlugin : IPlugin
{
    public const string Id = "devhub.git";

    /// <summary>git.exe, looked up once (null when git isn't installed or too old). GitHub uses it too.</summary>
    public static readonly Lazy<GitExecutable?> Executable = new(GitExecutable.TryLocate);

    // No requirements: works without Projects (Open Repository…), links up with it when both are on.
    public PluginInfo Info { get; } = new(Id, "Git",
        "A Fork-style Git client: changes, staging per line, commits, history graph, branches, remotes, merge and rebase, stashes, blame.",
        []) { Category = "Version control", Order = 200, Presets = ["Git client"] };

    /// <summary>Git's own design tokens (diff and graph colors, list widths), merged into the app's resources.</summary>
    public IEnumerable<Uri> Resources => [new("avares://DevHub.Git/Themes/GitTokens.axaml")];

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        // One shared command log and runner. The runner is only registered when git is installed
        // (and new enough); without it, repository tabs show how to install Git.
        services.AddSingleton<IGitCommandLog, GitCommandLog>();
        if (Executable.Value is { } git)
        {
            services.AddSingleton(git);
            services.AddSingleton<GitRunner>(sp => new GitRunner(git, sp.GetRequiredService<IGitCommandLog>()));
        }

        services.AddSingleton<GitConsoleViewModel>();
        services.AddSingleton<ToolWindowViewModelBase>(sp => sp.GetRequiredService<GitConsoleViewModel>());

        // One tab per repository, plus diff and blame tabs, created on demand / restored by id.
        // The layout (panel widths, tree / flat, unified / side-by-side) is shared by all of them.
        services.AddSingleton(sp => new RepositoryTabLayout(sp.GetRequiredService<ISettingsStore>()));
        services.AddSingleton<RepositoryTabServices>();
        services.AddSingleton<IDocumentFactory, RepositoryDocumentFactory>();
        services.AddSingleton<IDocumentFactory, DiffDocumentFactory>();
        services.AddSingleton<IDocumentFactory, BlameDocumentFactory>();
        services.AddSingleton<CloneOperations>();
        services.AddSingleton<OpenRepositoryOperation>();
        services.AddSingleton<RepositoryFileOpener>();
        services.AddSingleton<TerminalLauncher>();

        services.AddSingleton<ActiveRepositoryService>();
        services.AddSingleton<TitleBarWidgetViewModelBase, BranchWidgetViewModel>();
        // Offered to Projects (DevHub.Integration): double-click a project → its repository tab.
        services.AddSingleton<IProjectOpener, GitProjectOpener>();
        services.AddSingleton<IActionProvider, GitActionProvider>();
    }
}
