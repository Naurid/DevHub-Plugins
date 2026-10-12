using CommunityToolkit.Mvvm.Input;
using DevHub.Actions;
using DevHub.Controls;
using DevHub.Sdk.Core.Plugins;
using DevHub.Services;
using DevHub.ViewModels.ToolWindows;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.HelloWorldPlugin))]

namespace DevHub.Plugins;

/// <summary>The sample plugin: its id must equal plugin.json's.</summary>
public sealed class HelloWorldPlugin : IPlugin
{
    public PluginInfo Info { get; } = new("naurid.hello-world", "Hello World",
        "A sample plugin: a tool window that says hello, and a Hello action in Find Action.", [])
    {
        Category = "Samples",
        Order = 5000,
    };

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<HelloToolWindowViewModel>();
        services.AddSingleton<ToolWindowViewModelBase>(sp => sp.GetRequiredService<HelloToolWindowViewModel>());
        services.AddSingleton<IActionProvider, HelloActionProvider>();
    }
}

/// <summary>"Say Hello" in Find Action (Ctrl+Shift+A): a status-bar message.</summary>
public sealed class HelloActionProvider(IStatusService status) : IActionProvider
{
    public IEnumerable<AppAction> GetActions()
    {
        yield return new AppAction("HelloWorld.SayHello", "Say Hello", new RelayCommand(() => status.Show("Hello from a marketplace plugin!")))
        {
            Category = "Hello World",
            IconKind = IconKind.Info,
        };
    }
}
