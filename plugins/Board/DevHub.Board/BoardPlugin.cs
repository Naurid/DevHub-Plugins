using DevHub.Actions;
using DevHub.Actions.Providers;
using DevHub.Board;
using DevHub.Board.Core.Storage;
using DevHub.Sdk.Core.Plugins;
using DevHub.Services.Board;
using DevHub.ViewModels.Board;
using DevHub.ViewModels.Dialogs.Settings;
using DevHub.ViewModels.Documents;
using DevHub.ViewModels.ToolWindows;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.BoardPlugin))]

namespace DevHub.Plugins;

/// <summary>
/// Board: Jira-style planning without a server (docs/BOARD_PLAN.md). Board tabs (board, backlog, search), issue tabs,
/// board settings, the Boards tool window. Needs no other plugin; Board.Scrum, Board.Git, Board.Reports and
/// Board.Timeline add to it through the extension points in Extensibility/.
/// </summary>
public sealed class BoardPlugin : IPlugin
{
    public const string Id = "devhub.board";

    public PluginInfo Info { get; } = new(Id, "Board",
        "Jira-style planning without a server: epics, stories, tasks, bugs and sub-tasks, story points and time tracking, boards with columns you shape yourself, a ranked backlog and search. Boards are plain files in a folder.",
        []) { Category = "Planning", Order = 500, Presets = ["Planning"] };

    public IEnumerable<Uri> Resources => [new("avares://DevHub.Board/Themes/BoardTokens.axaml")];

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        // Where boards live: a folder (Board.Git adds the repository branch).
        services.AddSingleton<IBoardStoreProvider, FolderBoardStoreProvider>();
        services.AddSingleton<BoardService>();
        services.AddSingleton<BoardUiServices>();
        services.AddSingleton<BoardOpener>();

        // Pages of a board tab (other plugins add theirs, or replace one: Board.Scrum's backlog with sprints).
        services.AddSingleton<IBoardPageProvider, BoardViewPageProvider>();
        services.AddSingleton<IBoardPageProvider, BacklogPageProvider>();
        services.AddSingleton<IBoardPageProvider, SearchPageProvider>();

        // Tabs: one per board, one per issue opened on its own, one per board's settings; restored at startup.
        services.AddSingleton<IDocumentFactory, BoardDocumentFactory>();
        services.AddSingleton<IDocumentFactory, IssueDocumentFactory>();
        services.AddSingleton<IDocumentFactory, ProjectSettingsDocumentFactory>();

        services.AddSingleton<BoardsToolWindowViewModel>();
        services.AddSingleton<ToolWindowViewModelBase>(sp => sp.GetRequiredService<BoardsToolWindowViewModel>());
        services.AddSingleton<IActionProvider, BoardActionProvider>();
        services.AddTransient<SettingsPageViewModelBase, BoardSettingsPageViewModel>();
    }
}
