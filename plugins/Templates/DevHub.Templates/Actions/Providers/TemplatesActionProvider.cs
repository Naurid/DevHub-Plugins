using CommunityToolkit.Mvvm.Input;
using DevHub.Controls;
using DevHub.ViewModels.Documents;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Actions.Providers;

/// <summary>
/// Template library actions, in the File menu after "Templates". Reload and Open Templates Folder wrap
/// the New Project browser's commands; Save acts on the template editor tab in front.
/// </summary>
public sealed class TemplatesActionProvider(GenerateViewModel generate, TemplatesViewModel templates, IDocumentService documents)
    : IActionProvider
{
    public IEnumerable<AppAction> GetActions()
    {
        yield return new AppAction(TemplatesActionIds.NewTemplate, L.T("New Template…"), templates.NewTemplateCommand)
        {
            Category = "Templates",
            IconKind = IconKind.Add,
            Menu = "File",
            MenuOrder = 101,
            Description = L.T("Start a template in your library and open it in the editor."),
        };

        yield return new AppAction(TemplatesActionIds.Save, L.T("Save Template"),
            new AsyncRelayCommand(() => documents.Active is TemplateEditorViewModel editor ? editor.SaveAsync() : Task.CompletedTask))
        {
            Category = "Templates",
            Gesture = "Ctrl+S",
            Menu = "File",
            MenuOrder = 102,
            Description = L.T("Save the template open in the editor tab in front."),
        };

        yield return new AppAction(TemplatesActionIds.Reload, L.T("Reload Templates"), generate.Browser.ReloadCommand)
        {
            Category = "Templates",
            IconKind = IconKind.Refresh,
            Menu = "File",
            MenuOrder = 103,
            Description = L.T("Read every template library again (after you changed a template.json by hand)."),
        };

        yield return new AppAction(TemplatesActionIds.OpenLibraryFolder, L.T("Open Templates Folder"), generate.Browser.OpenLibraryFolderCommand)
        {
            Category = "Templates",
            IconKind = IconKind.Folder,
            Menu = "File",
            MenuOrder = 104,
            Description = L.T("Open your personal template library in Explorer (one folder per template)."),
        };
    }
}
