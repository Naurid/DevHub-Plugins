using CommunityToolkit.Mvvm.Input;
using DevHub.Actions;
using DevHub.Board;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Sdk.Core.Plugins;
using DevHub.Services;
using DevHub.ViewModels.Documents;
using DevHub.ViewModels.Scrum;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.BoardScrumPlugin))]

namespace DevHub.Plugins;

/// <summary>Board.Scrum: sprint planning, Start / Complete / Edit Sprint, capacity. Requires Board.</summary>
public sealed class BoardScrumPlugin : IPlugin
{
    public const string Id = "devhub.board.scrum";

    public PluginInfo Info { get; } = new(Id, "Board Scrum",
        "Sprints for boards: plan sprints in the backlog by dragging issues into them, start them with a goal and dates, see capacity and points, complete them and move what's left.",
        [BoardPlugin.Id]) { Category = "Planning", Order = 510, Presets = ["Planning"] };

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<IBoardPageProvider, ScrumPlanningPageProvider>();
        services.AddSingleton<IActionProvider, ScrumActionProvider>();
    }
}

/// <summary>Sprint actions for the board in front (Board menu, Find Action).</summary>
public sealed class ScrumActionProvider(IServiceProvider services, IStatusService status) : IActionProvider
{
    public IEnumerable<AppAction> GetActions()
    {
        yield return new AppAction("Board.CreateSprint", "Create Sprint", new AsyncRelayCommand(() => OnBoard(b => b.Context!.RunAsync(new CreateSprint()))))
        {
            Category = "Board",
            Menu = "Board",
            MenuOrder = 150,
            Description = "Plan another sprint on the board in front.",
        };
        yield return new AppAction("Board.SprintPlanning", "Sprint Planning", new AsyncRelayCommand(() => OnBoard(b =>
        {
            b.SelectedPage = b.Pages.FirstOrDefault(p => p.Id == "scrum-planning") ?? b.SelectedPage;
            return Task.CompletedTask;
        })))
        {
            Category = "Board",
            Menu = "Board",
            MenuOrder = 151,
            Description = "The backlog with a list per sprint (Scrum boards).",
        };
    }

    // Looked up when used: action providers are built before the document service exists.
    private async Task OnBoard(Func<BoardDocumentViewModel, Task> action)
    {
        var documents = (IDocumentService)services.GetService(typeof(IDocumentService))!;
        if (documents.Active is not BoardDocumentViewModel { Context: not null } board)
        {
            status.Show("Open a board first.");
            return;
        }
        if (board.SelectedBoard?.Kind != BoardKind.Scrum)
            status.Show("Sprints are planned on Scrum boards (Board Settings → Scrum board).");
        await action(board);
    }
}
