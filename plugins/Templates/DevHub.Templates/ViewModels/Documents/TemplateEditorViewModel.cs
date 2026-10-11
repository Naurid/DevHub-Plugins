using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Controls;
using DevHub.Integration;
using DevHub.Services;
using DevHub.Templates.Core;
using DevHub.Templates.Core.Editing;
using DevHub.ViewModels.Dialogs;
using DevHub.ViewModels.Templates.Editor;

namespace DevHub.ViewModels.Documents;

/// <summary>
/// One template's editor tab ("Template:&lt;folder&gt;"): sections on the left (General, Variables,
/// Base Command, Steps, Post-create, Files, template.json), the selected one on the right. Form
/// edits are kept in the tab until Save (Ctrl+S); Files changes happen on disk right away.
/// A built-in template is shown read-only with Duplicate. A template.json that can't be read opens
/// on the template.json section, so it can be fixed here.
/// </summary>
public sealed partial class TemplateEditorViewModel : DocumentViewModelBase
{
    public const string IdPrefix = "Template:";

    private readonly TemplatesService _templates;
    private readonly IDialogService _dialogs;
    private readonly IStatusService _status;
    private readonly Func<IProjectFileEditor?> _editor;
    private string _folder;
    private bool _loading;

    /// <summary>The manifest as it is on disk (what Build starts from, so unknown fields survive).</summary>
    private TemplateManifest _saved = new();

    public TemplateEditorViewModel(string folder, TemplateLibrary library, TemplatesService templates, IDialogService dialogs,
        IStatusService status, Func<IProjectFileEditor?> editor)
    {
        _folder = folder;
        _templates = templates;
        _dialogs = dialogs;
        _status = status;
        _editor = editor;
        Library = library;
        Entry = new TemplateEntry(library, folder, null, []);
    }

    /// <summary>The document id for a template folder (normalised, so one tab per template).</summary>
    public static string IdFor(string folder) => IdPrefix + Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    public override string Id => IdFor(_folder);
    public override string Title => Entry.Manifest?.Name is { Length: > 0 } name ? name : Path.GetFileName(_folder);
    public override string Icon => IconKind.Template;
    public override bool FillsEditorArea => true;
    public override bool HasUnsavedChanges => IsModified;

    public TemplateLibrary Library { get; }
    public bool IsReadOnly => Library.IsReadOnly;

    /// <summary>The template as last loaded or saved.</summary>
    [ObservableProperty] private TemplateEntry _entry;

    [ObservableProperty] private bool _isLoaded;

    /// <summary>The template's folder is gone (deleted from the Templates tab or outside DevHub).</summary>
    [ObservableProperty] private bool _isGone;

    public ObservableCollection<EditorSectionViewModel> Sections { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SectionEnabled))]
    private EditorSectionViewModel? _selectedSection;

    /// <summary>Read-only templates: only Files (to look and open) and template.json (read-only text) work.</summary>
    public bool SectionEnabled => !IsReadOnly || SelectedSection?.Part is FilesPartViewModel or JsonPartViewModel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private bool _isModified;

    /// <summary>Why the last save (or applying template.json) didn't work.</summary>
    [ObservableProperty] private IReadOnlyList<string> _problems = [];

    public bool HasProblems => Problems.Count > 0;

    public string LibraryText => IsReadOnly ? $"{Library.Name} (read-only)" : Library.Name;
    public string FolderText => _folder;

    public bool CanOpenInVsCode => _templates.VsCodePath is not null;

    private GeneralPartViewModel? _general;
    private VariablesPartViewModel? _variables;
    private CommandPartViewModel? _command;
    private StepsPartViewModel? _steps;
    private PostCreatePartViewModel? _postCreate;
    private FilesPartViewModel? _files;
    private JsonPartViewModel? _json;

    partial void OnProblemsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasProblems));

    partial void OnIsModifiedChanged(bool value) => IsDirty = value;

    protected override void OnActivationChanged(bool isActive)
    {
        if (!isActive)
            return;
        if (!IsLoaded)
            _ = LoadAsync();
        else if (!File.Exists(Path.Combine(_folder, TemplateStore.ManifestFileName)))
            IsGone = true;
        else
            _files?.Refresh(); // files may have changed in the external editor
    }

    /// <summary>Reads template.json from disk and fills the sections (Revert does the same).</summary>
    private async Task LoadAsync(string? keepSection = null)
    {
        if (!File.Exists(Path.Combine(_folder, TemplateStore.ManifestFileName)))
        {
            IsGone = true;
            return;
        }
        var entry = await TemplateStore.LoadAsync(Library, _folder);
        var raw = await File.ReadAllTextAsync(Path.Combine(_folder, TemplateStore.ManifestFileName));
        Entry = entry;
        _saved = entry.Manifest ?? new TemplateManifest();
        Fill(entry.Manifest, raw, keepSection);
        Problems = entry.Problems;
        IsModified = false;
        IsLoaded = true;
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>The sections for a manifest; without one (unreadable JSON) only Files and template.json.</summary>
    private void Fill(TemplateManifest? manifest, string json, string? keepSection)
    {
        _loading = true;
        try
        {
            var keep = keepSection ?? SelectedSection?.Name;
            Sections.Clear();
            _json = new JsonPartViewModel(IsReadOnly, MarkModified);
            if (manifest is not null)
            {
                var languages = NewTemplate.KnownLanguages.Contains(manifest.Language)
                    ? NewTemplate.KnownLanguages
                    : [manifest.Language, .. NewTemplate.KnownLanguages];
                _general = new GeneralPartViewModel(manifest, languages, MarkModified);
                _variables = new VariablesPartViewModel(manifest, MarkModified);
                _command = new CommandPartViewModel(manifest.BaseCommand, MarkModified);
                _steps = new StepsPartViewModel(manifest, MarkModified);
                _postCreate = new PostCreatePartViewModel(manifest, () => _variables?.BoolNames ?? [], MarkModified);
                Sections.Add(new EditorSectionViewModel("General", IconKind.Settings, _general));
                Sections.Add(new EditorSectionViewModel("Variables", IconKind.Edit, _variables));
                Sections.Add(new EditorSectionViewModel("Base Command", IconKind.Terminal, _command));
                Sections.Add(new EditorSectionViewModel("Steps", IconKind.Folder, _steps));
                Sections.Add(new EditorSectionViewModel("Post-create", IconKind.Run, _postCreate));
            }
            else
            {
                _general = null;
                _variables = null;
                _command = null;
                _steps = null;
                _postCreate = null;
            }
            _files ??= new FilesPartViewModel(() => Entry, () => _variables?.Names ?? [], _templates, _dialogs, _status, _editor);
            Sections.Add(new EditorSectionViewModel("Files", IconKind.Folder, _files));
            Sections.Add(new EditorSectionViewModel("template.json", IconKind.Template, _json));
            _json.Show(json);
            _files.Refresh();

            SelectedSection = Sections.FirstOrDefault(s => s.Name == keep)
                              ?? (manifest is null ? Sections.Last() : Sections.First());
        }
        finally
        {
            _loading = false;
        }
    }

    private void MarkModified()
    {
        if (!_loading && !IsReadOnly)
            IsModified = true;
    }

    /// <summary>
    /// Leaving template.json with edits applies them to the forms; entering it shows the forms as JSON.
    /// </summary>
    partial void OnSelectedSectionChanged(EditorSectionViewModel? oldValue, EditorSectionViewModel? newValue)
    {
        if (_loading)
            return;
        if (oldValue?.Part is JsonPartViewModel { IsEdited: true })
            ApplyJson();
        if (newValue?.Part is JsonPartViewModel json && !json.IsEdited && BuildManifest() is { } manifest)
            json.Show(ManifestSerializer.Write(manifest));
    }

    /// <summary>Puts the typed JSON into the forms. False (and the problem shown) when it can't be read.</summary>
    private bool ApplyJson()
    {
        if (_json is not { IsEdited: true } json)
            return true;
        var (manifest, problems) = ManifestText.Parse(json.Text);
        if (manifest is null)
        {
            json.Error = problems[0];
            Problems = ["template.json: " + problems[0] + " (your text is kept in the template.json section)"];
            return false;
        }
        var text = json.Text;
        Fill(manifest, text, SelectedSection?.Name);
        _json!.Show(text);
        Problems = problems;
        IsModified = true;
        return true;
    }

    /// <summary>The manifest from the forms, or null when there are no forms (template.json unreadable).</summary>
    private TemplateManifest? BuildManifest()
    {
        if (_general is null)
            return null;
        return _general.Build(_saved) with
        {
            Variables = _variables!.Build(),
            BaseCommand = _command!.Build(),
            Steps = _steps!.Build(),
            PostCreate = _postCreate!.Build(),
        };
    }

    private bool CanSave() => IsModified && !IsReadOnly;

    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync()
    {
        if (!CanSave())
            return;
        if (!ApplyJson() || BuildManifest() is not { } manifest)
        {
            if (!HasProblems)
                Problems = ["template.json can't be read; fix it in the template.json section first."];
            return;
        }

        TemplateStoreResult result;
        try
        {
            result = await TemplateStore.SaveAsync(Entry, manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problems = ["Couldn't save: " + ex.Message];
            return;
        }
        if (result.Entry is not { } saved)
        {
            Problems = result.Errors;
            _status.Show($"{Title} wasn't saved: fix the problems shown at the top.");
            return;
        }

        _folder = saved.Folder; // a new id renames the folder (and this tab's id)
        OnPropertyChanged(nameof(FolderText));
        await LoadAsync(SelectedSection?.Name);
        _templates.NotifyChanged();
        _status.Show($"Saved {Title}.");
    }

    [RelayCommand(CanExecute = nameof(IsModified))]
    private async Task RevertAsync()
    {
        var discard = await _dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Revert",
            $"Throw away your changes to {Title} and read template.json again?", "Revert", []));
        if (discard)
            await LoadAsync(SelectedSection?.Name);
    }

    /// <summary>Read-only template: a copy in the personal library, opened in its own tab.</summary>
    [RelayCommand]
    private async Task DuplicateAsync()
    {
        var copy = await TemplateStore.DuplicateAsync(Entry, _templates.PersonalLibrary);
        if (copy.Entry is not { } copied)
        {
            Problems = copy.Errors;
            return;
        }
        _templates.NotifyChanged();
        _status.Show($"Copied {Title} to {copied.Reference}.");
        OpenDocument?.Invoke(IdFor(copied.Folder));
    }

    [RelayCommand]
    private void ShowInExplorer() => TemplatesService.Reveal(_folder);

    [RelayCommand]
    private void OpenInVsCode() => _templates.OpenInVsCode(_folder);

    [RelayCommand]
    private void CloseGone() => CloseCommand?.Execute(null);

    public override async Task<bool> ConfirmCloseAsync() =>
        !IsModified || await _dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Unsaved Changes",
            $"{Title} has changes that aren't saved. Close it and lose them?", "Discard", []));
}

/// <summary>A section of the editor (left list): its name, icon and the part shown on the right.</summary>
public sealed class EditorSectionViewModel(string name, string iconKind, ViewModelBase part) : ViewModelBase
{
    public string Name { get; } = name;
    public string IconKind { get; } = iconKind;
    public ViewModelBase Part { get; } = part;

    public override string ToString() => Name;
}

/// <summary>Creates template editor tabs from ids like "Template:C:\Users\me\AppData\Roaming\DevHub\templates\unity-game".</summary>
public sealed class TemplateEditorFactory(TemplatesService templates, IDialogService dialogs, IStatusService status,
    Func<IProjectFileEditor?> editor) : IDocumentFactory
{
    public string IdPrefix => TemplateEditorViewModel.IdPrefix;

    public DocumentViewModelBase? Create(string id)
    {
        if (!id.StartsWith(IdPrefix, StringComparison.Ordinal))
            return null;
        var folder = id[IdPrefix.Length..];
        // Deleted, or its library isn't in use any more → skip (the DocumentManager tells the user).
        if (!File.Exists(Path.Combine(folder, TemplateStore.ManifestFileName)) || templates.LibraryOf(folder) is not { } library)
            return null;
        return new TemplateEditorViewModel(folder, library, templates, dialogs, status, editor);
    }
}
