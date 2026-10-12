using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Sdk.Core.Settings;
using DevHub.Services;
using DevHub.Templates.Core;
using DevHub.Templates.Core.Editing;

namespace DevHub.ViewModels.Dialogs.Settings;

/// <summary>
/// Settings → Templates: the {{Author}} name; where new projects go (a default folder, and rules per
/// language and/or category: "Unity → D:\Unity"); the personal library's folder and extra libraries.
/// </summary>
public sealed partial class TemplatesSettingsPageViewModel : SettingsPageViewModelBase
{
    private readonly ISettingsStore _store;
    private readonly IDialogService _dialogs;
    private readonly TemplatesService _templates;

    public TemplatesSettingsPageViewModel(ISettingsStore store, IDialogService dialogs, TemplatesService templates)
    {
        _store = store;
        _dialogs = dialogs;
        _templates = templates;
        var settings = store.LoadSection<TemplatesSettings>();
        _author = settings.Author ?? "";
        _defaultProjectFolder = settings.DefaultProjectFolder ?? "";
        _personalLibrary = settings.PersonalLibrary ?? "";
        foreach (var rule in settings.ProjectFolderRules)
            Rules.Add(new FolderRuleRowViewModel(rule.Language ?? "", rule.Category ?? "", rule.Folder, dialogs, MarkModified, RemoveRule));
        foreach (var folder in settings.ExtraLibraries)
            ExtraLibraries.Add(new LibraryRowViewModel(folder, RemoveLibrary));
    }

    public override string Title => "Templates";
    public override int Order => 300;
    public override string Keywords => "templates author project folder location language category library new project";

    public string AuthorPlaceholder => Environment.UserName;
    public string PersonalLibraryPlaceholder => TemplateLibraries.DefaultPersonalRoot(
        Environment.GetEnvironmentVariable("DEVHUB_DATA_DIR") is { Length: > 0 } dir ? dir : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    public IReadOnlyList<string> Languages => NewTemplate.KnownLanguages;

    [ObservableProperty] private string _author;
    [ObservableProperty] private string _defaultProjectFolder;
    [ObservableProperty] private string _personalLibrary;

    public ObservableCollection<FolderRuleRowViewModel> Rules { get; } = [];
    public ObservableCollection<LibraryRowViewModel> ExtraLibraries { get; } = [];

    partial void OnAuthorChanged(string value) => MarkModified();
    partial void OnDefaultProjectFolderChanged(string value) => MarkModified();
    partial void OnPersonalLibraryChanged(string value) => MarkModified();

    [RelayCommand]
    private async Task BrowseDefaultFolderAsync()
    {
        if (await _dialogs.PickFolderAsync("New projects go to") is { } folder)
            DefaultProjectFolder = folder;
    }

    [RelayCommand]
    private async Task BrowsePersonalLibraryAsync()
    {
        if (await _dialogs.PickFolderAsync("Your template library") is { } folder)
            PersonalLibrary = folder;
    }

    [RelayCommand]
    private void AddRule()
    {
        Rules.Add(new FolderRuleRowViewModel("", "", "", _dialogs, MarkModified, RemoveRule));
        MarkModified();
    }

    [RelayCommand]
    private async Task AddLibraryAsync()
    {
        if (await _dialogs.PickFolderAsync("A folder of templates") is not { } folder)
            return;
        ExtraLibraries.Add(new LibraryRowViewModel(folder, RemoveLibrary));
        MarkModified();
    }

    private void RemoveRule(FolderRuleRowViewModel row)
    {
        Rules.Remove(row);
        MarkModified();
    }

    private void RemoveLibrary(LibraryRowViewModel row)
    {
        ExtraLibraries.Remove(row);
        MarkModified();
    }

    public override void Apply()
    {
        _store.UpdateSection<TemplatesSettings>(s =>
        {
            s.Author = string.IsNullOrWhiteSpace(Author) ? null : Author.Trim();
            s.DefaultProjectFolder = string.IsNullOrWhiteSpace(DefaultProjectFolder) ? null : DefaultProjectFolder.Trim();
            s.PersonalLibrary = string.IsNullOrWhiteSpace(PersonalLibrary) ? null : PersonalLibrary.Trim();
            s.ProjectFolderRules = Rules
                .Where(r => r.Folder.Trim().Length > 0 && (r.Language.Trim().Length > 0 || r.Category.Trim().Length > 0))
                .Select(r => new ProjectFolderRule
                {
                    Language = r.Language.Trim() is { Length: > 0 } language ? language : null,
                    Category = r.Category.Trim() is { Length: > 0 } category ? category : null,
                    Folder = r.Folder.Trim(),
                })
                .ToList();
            s.ExtraLibraries = ExtraLibraries.Select(l => l.Folder).ToList();
        });
        _templates.NotifyChanged(); // the lists read the libraries again
        IsModified = false;
    }
}

/// <summary>One "language and/or category → folder" rule.</summary>
public sealed partial class FolderRuleRowViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private readonly Action _changed;

    public FolderRuleRowViewModel(string language, string category, string folder, IDialogService dialogs, Action changed,
        Action<FolderRuleRowViewModel> remove)
    {
        _language = language;
        _category = category;
        _folder = folder;
        _dialogs = dialogs;
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    [ObservableProperty] private string _language;
    [ObservableProperty] private string _category;
    [ObservableProperty] private string _folder;

    public RelayCommand RemoveCommand { get; }

    partial void OnLanguageChanged(string value) => _changed();
    partial void OnCategoryChanged(string value) => _changed();
    partial void OnFolderChanged(string value) => _changed();

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _dialogs.PickFolderAsync("Projects of this kind go to") is { } folder)
            Folder = folder;
    }

    public override string ToString() => $"{Language} {Category} → {Folder}";
}

/// <summary>An extra template library folder.</summary>
public sealed class LibraryRowViewModel : ViewModelBase
{
    public LibraryRowViewModel(string folder, Action<LibraryRowViewModel> remove)
    {
        Folder = folder;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Folder { get; }
    public RelayCommand RemoveCommand { get; }

    public override string ToString() => Folder;
}
