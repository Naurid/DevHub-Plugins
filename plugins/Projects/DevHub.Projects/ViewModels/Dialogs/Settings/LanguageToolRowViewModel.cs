using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Projects.Core;
using DevHub.Sdk.Core.Settings;
using DevHub.Services;

namespace DevHub.ViewModels.Dialogs.Settings;

/// <summary>
/// One row on the Settings → Open With page: a language and the tool chosen to open it. Raises
/// <see cref="_onChanged"/> whenever the user changes the selection or picks a custom executable,
/// so the page can mark itself modified. A custom tool is launched on the repo folder (no arguments).
/// </summary>
public partial class LanguageToolRowViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private readonly IDialogService _dialogs;

    public string LanguageId { get; }
    public string LanguageDisplay { get; }
    public IReadOnlyList<DevTool> AvailableTools { get; }

    [ObservableProperty]
    private DevTool _selectedTool;

    [ObservableProperty]
    private string? _customExe;

    /// <summary>Drives visibility of the custom-executable row.</summary>
    public bool IsCustom => SelectedTool.Id == ToolIds.Custom;

    public LanguageToolRowViewModel(
        ProjectLanguage language,
        IReadOnlyList<DevTool> tools,
        IReadOnlyList<string> availableIds,
        LanguageToolChoice? current,
        IDialogService dialogs,
        Action onChanged)
    {
        LanguageId = language.Id;
        LanguageDisplay = language.DisplayName;
        AvailableTools = tools;
        _dialogs = dialogs;
        _onChanged = onChanged;

        // Initialise via backing fields so the change callbacks (and a save) don't fire on load.
        var initialId = current?.ToolId ?? DefaultToolPolicy.DefaultFor(language.Id, availableIds);
        _selectedTool = tools.FirstOrDefault(t => t.Id == initialId)
                        ?? tools.First(t => t.Id == ToolIds.OsDefault);
        _customExe = current?.CustomExe;
    }

    public LanguageToolChoice ToChoice() => new()
    {
        ToolId = SelectedTool.Id,
        CustomExe = CustomExe
    };

    [RelayCommand]
    private async Task BrowseExeAsync()
    {
        var exe = await _dialogs.PickFileAsync("Choose an executable", "Programs", new[] { "*.exe" });
        if (!string.IsNullOrEmpty(exe))
            CustomExe = exe; // triggers OnCustomExeChanged → page modified
    }

    partial void OnSelectedToolChanged(DevTool value)
    {
        OnPropertyChanged(nameof(IsCustom));
        _onChanged();
    }

    partial void OnCustomExeChanged(string? value) => _onChanged();
}
