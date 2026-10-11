using DevHub.Controls;
using DevHub.ViewModels.Documents;

namespace DevHub.Actions.Providers;

/// <summary>
/// Template library actions, in the File menu after "Templates". They wrap the New Project
/// browser's commands, so the buttons above the list and the menu do the same thing.
/// </summary>
public sealed class TemplatesActionProvider(GenerateViewModel generate) : IActionProvider
{
    public IEnumerable<AppAction> GetActions()
    {
        yield return new AppAction(TemplatesActionIds.Reload, "Reload Templates", generate.Browser.ReloadCommand)
        {
            Category = "Templates",
            IconKind = IconKind.Refresh,
            Menu = "File",
            MenuOrder = 101,
            Description = "Read every template library again (after you changed a template.json by hand).",
        };

        yield return new AppAction(TemplatesActionIds.OpenLibraryFolder, "Open Templates Folder", generate.Browser.OpenLibraryFolderCommand)
        {
            Category = "Templates",
            IconKind = IconKind.Folder,
            Menu = "File",
            MenuOrder = 102,
            Description = "Open your personal template library in Explorer (one folder per template).",
        };
    }
}
