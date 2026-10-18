using DevHub.Controls;
using DevHub.ViewModels.ToolWindows;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Actions.Providers;

/// <summary>
/// Projects tool window actions (shown as "Projects"; ids keep the old "Repositories." prefix so saved
/// shortcuts stay valid). They wrap the tool window's existing commands, so CanExecute (e.g. "Rescan"
/// is disabled while scanning or with no folders) is shared with the tool window's own buttons.
/// </summary>
public sealed class RepositoryActionProvider(RepositoriesViewModel repositories) : IActionProvider
{
    public IEnumerable<AppAction> GetActions()
    {
        yield return new AppAction(ProjectsActionIds.AddScanFolder, L.T("Add Folder…"), repositories.AddFolderCommand)
        {
            Category = "Projects",
            IconKind = IconKind.Add,
            Menu = "Projects",
            MenuOrder = 0,
            WelcomeOrder = 40,
            Description = L.T("Choose a folder to scan for projects (git repositories)."),
        };

        yield return new AppAction(ProjectsActionIds.RescanRepositories, L.T("Rescan"), repositories.ScanCommand)
        {
            Category = "Projects",
            IconKind = IconKind.Refresh,
            Gesture = "F5",
            Menu = "Projects",
            MenuOrder = 100,
            Description = L.T("Scan the project folders for git repositories again."),
        };
    }
}
