using DevHub.GitHub.Core;
using DevHub.Sdk.Core.Plugins;
using DevHub.Services;
using DevHub.ViewModels.Dialogs.Settings;
using DevHub.ViewModels.Git;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.GitHubPlugin))]

namespace DevHub.Plugins;

/// <summary>GitHub: the account (through Git Credential Manager), Publish to GitHub, your repositories in Clone.</summary>
public sealed class GitHubPlugin : IPlugin
{
    public const string Id = "devhub.github";

    public PluginInfo Info { get; } = new(Id, "GitHub",
        "Sign in to GitHub through Git Credential Manager, publish a repository to your account, clone your repositories, show avatars.",
        [GitPlugin.Id]) { Category = "Version control", Order = 300, Presets = ["Git client"] };

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        // The account comes from Git Credential Manager (git), so the API needs git too.
        if (GitPlugin.Executable.Value is not null)
        {
            services.AddSingleton<GitCredentialManager>();
            services.AddSingleton(new GitHubApi(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
            services.AddSingleton<GitHubService>();
        }
        services.AddSingleton<GitHubSession>();
        // Offered to Git (Git doesn't know GitHub): Publish, your avatar, your repositories in Clone.
        services.AddSingleton<IRemoteHost, GitHubRemoteHost>();
        services.AddTransient<SettingsPageViewModelBase, GitHubSettingsPageViewModel>();
    }
}
