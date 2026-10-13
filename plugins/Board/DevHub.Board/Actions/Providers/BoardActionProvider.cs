using CommunityToolkit.Mvvm.Input;
using DevHub.Controls;
using DevHub.Services;
using DevHub.Services.Board;
using DevHub.ViewModels.Documents;

namespace DevHub.Actions.Providers;

/// <summary>Ids of the Board plugin's actions (XAML uses the same strings: <c>{a:Action Board.New}</c>).</summary>
public static class BoardActionIds
{
    public const string New = "Board.New";
    public const string Open = "Board.Open";
    public const string ForProject = "Board.ForProject";
    public const string CreateIssue = "Board.CreateIssue";
    public const string Undo = "Board.Undo";
    public const string Settings = "Board.ProjectSettings";
    public const string Search = "Board.Search";
    public const string Backlog = "Board.Backlog";
}

/// <summary>
/// Board actions: in the Board main-menu group, Find Action, the Welcome view and shortcuts. Those that work on "the
/// board in front" use the active board tab (or the board of the issue tab in front).
/// </summary>
public sealed class BoardActionProvider(BoardOpener opener, IServiceProvider services, IStatusService status, IDialogService dialogs) : IActionProvider
{
    public IEnumerable<AppAction> GetActions()
    {
        yield return new AppAction(BoardActionIds.New, "New Board…", new AsyncRelayCommand(() => opener.NewBoardAsync()))
        {
            Category = "Board",
            IconKind = IconKind.Add,
            Menu = "Board",
            MenuOrder = 0,
            WelcomeOrder = 60,
            Description = "Create a board: epics, stories, sprints and columns you shape yourself, kept as plain files.",
        };
        yield return new AppAction(BoardActionIds.Open, "Open Board…", new AsyncRelayCommand(opener.OpenBoardAsync))
        {
            Category = "Board",
            IconKind = IconKind.Folder,
            Menu = "Board",
            MenuOrder = 1,
            WelcomeOrder = 61,
            Description = "Open the board in a folder.",
        };
        yield return new AppAction(BoardActionIds.ForProject, "Board of This Project", new AsyncRelayCommand(opener.BoardForProjectAsync))
        {
            Category = "Board",
            Menu = "Board",
            MenuOrder = 2,
            Description = "Open (or create) the board of the project or repository in front.",
        };
        yield return new AppAction(BoardActionIds.CreateIssue, "Create Issue…", new AsyncRelayCommand(() => OnBoard(b => b.CreateIssueCommand.ExecuteAsync(null))))
        {
            Category = "Board",
            IconKind = IconKind.Add,
            Gesture = "Ctrl+Alt+I",
            Menu = "Board",
            MenuOrder = 100,
            Description = "Create an issue on the board in front.",
        };
        yield return new AppAction(BoardActionIds.Backlog, "Show Backlog", new AsyncRelayCommand(() => OnBoard(b => ShowPage(b, "backlog"))))
        {
            Category = "Board",
            Menu = "Board",
            MenuOrder = 101,
        };
        yield return new AppAction(BoardActionIds.Search, "Search Issues", new AsyncRelayCommand(() => OnBoard(b => ShowPage(b, "search"))))
        {
            Category = "Board",
            IconKind = IconKind.Search,
            Menu = "Board",
            MenuOrder = 102,
            Description = "Search the board in front with the query language (type = bug AND assignee = currentUser()).",
        };
        yield return new AppAction("Board.GoToIssue", "Go to Issue…", new AsyncRelayCommand(() => OnBoard(GoToIssueAsync)))
        {
            Category = "Board",
            Menu = "Board",
            MenuOrder = 103,
            Description = "Open an issue of the board in front by its key (DEV-12).",
        };
        yield return new AppAction("Board.MyOpenIssues", "My Open Issues", new AsyncRelayCommand(() => OnBoard(b =>
        {
            b.ShowSearch("assignee = currentUser() AND statusCategory != Done ORDER BY priority, rank");
            return Task.CompletedTask;
        })))
        {
            Category = "Board",
            Menu = "Board",
            MenuOrder = 104,
            Description = "The open issues assigned to you on the board in front.",
        };
        yield return new AppAction(BoardActionIds.Undo, "Undo Last Board Change", new AsyncRelayCommand(() => OnBoard(b => b.UndoCommand.ExecuteAsync(null))))
        {
            Category = "Board",
            Menu = "Board",
            MenuOrder = 200,
        };
        yield return new AppAction(BoardActionIds.Settings, "Board Settings", new AsyncRelayCommand(() => OnBoard(b =>
        {
            b.ProjectSettingsCommand.Execute(null);
            return Task.CompletedTask;
        })))
        {
            Category = "Board",
            IconKind = IconKind.Settings,
            Menu = "Board",
            MenuOrder = 201,
            Description = "Workflows, statuses, issue types, priorities, fields, components, versions and people of the board in front.",
        };
    }


    private async Task GoToIssueAsync(BoardDocumentViewModel board)
    {
        var dialog = new ViewModels.Dialogs.TextInputDialogViewModel("Go to Issue", "Issue key", board.Context!.Project.Key + "-", "Open");
        if (!await dialogs.ShowDialogAsync(dialog))
            return;
        if (!board.GoToIssue(dialog.Text))
            status.Show($"There's no issue {dialog.Text} on this board.");
    }
    private static Task ShowPage(BoardDocumentViewModel board, string page)
    {
        board.SelectedPage = board.Pages.FirstOrDefault(p => p.Id == page) ?? board.SelectedPage;
        return Task.CompletedTask;
    }

    // The board tab in front, or the board tab of the issue / settings tab in front.
    private async Task OnBoard(Func<BoardDocumentViewModel, Task> action)
    {
        // Looked up when used: action providers are built before the document service exists.
        var documents = (IDocumentService)services.GetService(typeof(IDocumentService))!;
        var active = documents.Active;
        var board = active as BoardDocumentViewModel;
        if (board is null && active?.ContextPath is not null)
        {
            var location = active switch
            {
                IssueDocumentViewModel issue => issue.Location,
                ProjectSettingsDocumentViewModel settings => settings.Location,
                _ => null,
            };
            if (location is not null && documents.OpenById(BoardDocumentFactory.IdFor(location)))
                board = documents.Active as BoardDocumentViewModel;
        }
        if (board is null)
        {
            status.Show("Open a board first (Board → New Board… or Open Board…).");
            return;
        }
        if (board.Context is null)
        {
            status.Show("The board is still loading.");
            return;
        }
        await action(board);
    }
}
