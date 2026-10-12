using DevHub.Projects.Core;
using DevHub.Sdk.Core.Settings;
using DevHub.Services;

namespace DevHub.ViewModels.Dialogs.Settings;

/// <summary>
/// Settings → Open With: which tool opens repositories of each language. Only *overrides* are
/// saved; a row left at its policy default is removed from the file.
/// </summary>
public class OpenWithSettingsPageViewModel : SettingsPageViewModelBase
{
    private readonly ISettingsStore _store;
    private readonly IReadOnlyList<string> _availableIds;

    public override string Title => "Open With";
    public override int Order => 100;
    public override string Keywords => "tools editor ide rider visual studio code unity hub language program executable";

    public IReadOnlyList<LanguageToolRowViewModel> LanguageTools { get; }

    public OpenWithSettingsPageViewModel(ISettingsStore store, IToolLocator tools, IDialogService dialogs)
    {
        _store = store;
        var available = tools.GetAvailableTools();
        _availableIds = available.Select(t => t.Id).ToList();
        var saved = _store.LoadSection<ProjectsSettings>().LanguageTools;

        LanguageTools = KnownLanguages.All
            .Select(language =>
            {
                saved.TryGetValue(language.Id, out var choice);
                return new LanguageToolRowViewModel(language, available, _availableIds, choice, dialogs, MarkModified);
            })
            .ToList();
    }

    public override void Apply()
    {
        var overrides = new Dictionary<string, LanguageToolChoice>();
        foreach (var row in LanguageTools)
        {
            var choice = row.ToChoice();
            var isPlainDefault =
                choice.ToolId == DefaultToolPolicy.DefaultFor(row.LanguageId, _availableIds)
                && choice.ToolId != ToolIds.Custom;

            if (!isPlainDefault)
                overrides[row.LanguageId] = choice; // keep the file to overrides only
        }

        _store.UpdateSection<ProjectsSettings>(s => s.LanguageTools = overrides);
        IsModified = false;
    }
}
