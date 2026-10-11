using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Controls;
using DevHub.Services;
using DevHub.Templates.Core;
using DevHub.Templates.Core.Browsing;
using DevHub.Templates.Core.Editing;
using DevHub.ViewModels.Dialogs;
using DevHub.ViewModels.Templates;

namespace DevHub.ViewModels.Documents;

/// <summary>
/// The Templates tab: every template of every library (the same list as New Project, all kinds),
/// the selected one's summary, and what to do with it: Edit (its own tab), Use in New Project,
/// Duplicate, Delete, Show in Explorer. New Template… starts one in a writable library.
/// </summary>
public sealed partial class TemplatesViewModel : DocumentViewModelBase
{
    private readonly TemplatesService _templates;
    private readonly IDialogService _dialogs;
    private readonly IStatusService _status;
    private readonly GenerateViewModel _generate;

    public TemplatesViewModel(TemplatesService templates, IDialogService dialogs, IStatusService status, GenerateViewModel generate)
    {
        _templates = templates;
        _dialogs = dialogs;
        _status = status;
        _generate = generate;
        Browser = new TemplateBrowserViewModel(templates, OnSelected, Edit, TemplateBrowserViewModel.AllKinds);
    }

    public override string Title => "Templates";
    public override string Icon => IconKind.Template;
    public override bool FillsEditorArea => true;
    public override DocumentNavigation Navigation => new() { Menu = "File", MenuOrder = 100, WelcomeOrder = 30 };

    public TemplateBrowserViewModel Browser { get; }

    /// <summary>The selected template's summary, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedCommand), nameof(UseInNewProjectCommand), nameof(DuplicateCommand),
        nameof(DeleteCommand), nameof(ShowInExplorerCommand))]
    private TemplateSummaryViewModel? _selected;

    public bool HasSelection => Selected is not null;

    protected override void OnActivationChanged(bool isActive)
    {
        if (isActive)
            _ = Browser.ReloadIfChangedAsync();
    }

    private void OnSelected(TemplateEntry? template) => Selected = template is null ? null : new TemplateSummaryViewModel(template);

    private void Edit(TemplateEntry template) => OpenDocument?.Invoke(TemplateEditorViewModel.IdFor(template.Folder));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void EditSelected()
    {
        if (Selected is { } summary)
            Edit(summary.Template);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task UseInNewProjectAsync()
    {
        if (Selected is not { } summary)
            return;
        await _generate.SelectTemplateAsync(summary.Template.Reference);
        OpenDocument?.Invoke(_generate.Id);
    }

    [RelayCommand]
    private async Task NewTemplateAsync()
    {
        var writable = _templates.Libraries.Where(l => !l.IsReadOnly).ToList();
        var dialog = new NewTemplateDialogViewModel(writable);
        if (!await _dialogs.ShowDialogAsync(dialog))
            return;

        var manifest = NewTemplate.Create(dialog.Name, dialog.Id.Trim(), dialog.IsProject ? TemplateKind.Project : TemplateKind.Item, dialog.Language);
        TemplateStoreResult created;
        try
        {
            created = await TemplateStore.CreateAsync(dialog.Library!, manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            created = TemplateStoreResult.Failed(ex.Message);
        }
        if (created.Entry is not { } entry)
        {
            _status.Show("Couldn't create the template: " + string.Join(" ", created.Errors));
            return;
        }
        _templates.NotifyChanged();
        await Browser.ReloadAsync(select: entry.Reference);
        Edit(entry);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DuplicateAsync()
    {
        if (Selected is not { } summary)
            return;
        var copy = await TemplateStore.DuplicateAsync(summary.Template, _templates.PersonalLibrary);
        if (copy.Entry is not { } copied)
        {
            _status.Show("Couldn't duplicate: " + string.Join(" ", copy.Errors));
            return;
        }
        _templates.NotifyChanged();
        await Browser.ReloadAsync(select: copied.Reference);
        _status.Show($"Copied to {copied.Reference}.");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (Selected is not { } summary)
            return;
        var template = summary.Template;
        if (template.Library.IsReadOnly)
        {
            _status.Show($"{summary.Name} is built in and can't be deleted.");
            return;
        }
        var files = Directory.Exists(template.Folder) ? Directory.EnumerateFiles(template.Folder, "*", SearchOption.AllDirectories).Count() : 0;
        var ok = await _dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Delete Template",
            $"Delete {summary.Name}? Its folder and everything in it ({files} files, template.json included) are deleted permanently.",
            "Delete", [template.Folder]));
        if (!ok)
            return;
        var result = TemplateStore.Delete(template);
        if (!result.Succeeded)
        {
            _status.Show("Couldn't delete: " + string.Join(" ", result.Errors));
            return;
        }
        _templates.NotifyChanged();
        await Browser.ReloadAsync();
        _status.Show($"Deleted {summary.Name}.");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ShowInExplorer()
    {
        if (Selected is { } summary)
            TemplatesService.Reveal(summary.Template.Folder);
    }
}

/// <summary>What the Templates tab shows about the selected template.</summary>
public sealed class TemplateSummaryViewModel(TemplateEntry template) : ViewModelBase
{
    public TemplateEntry Template { get; } = template;
    private TemplateManifest? Manifest => Template.Manifest;

    public string Name => TemplateCatalog.DisplayName(Template);
    public string? Description => Manifest?.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string Language => Manifest?.Language ?? "";
    public IReadOnlyList<string> Categories => Manifest?.Categories ?? [];
    public string KindText => Manifest?.Kind == TemplateKind.Item ? "Item template: adds files to a folder" : "Project template: creates a project folder";
    public string LibraryText => Template.Library.IsReadOnly ? $"{Template.Library.Name} (read-only)" : Template.Library.Name;
    public string Folder => Template.Folder;
    public bool IsReadOnly => Template.Library.IsReadOnly;
    public bool IsBroken => !Template.IsUsable;
    public IReadOnlyList<string> Problems => Template.Problems;
    public bool IsProject => Manifest?.Kind == TemplateKind.Project;

    public string CommandText => Manifest?.BaseCommand is { } command
        ? string.Join(' ', new[] { command.Command }.Concat(command.Args))
        : "none (the template's files make the project)";

    public string CountsText => Manifest is not { } m ? ""
        : $"{Count(m.Variables.Count, "variable")} · {Count(m.Steps.Count, "step")} · {Count(m.PostCreate.Count, "post-create command")}"
          + $" · {Count(Template.HasContent ? Directory.EnumerateFiles(Template.ContentFolder, "*", SearchOption.AllDirectories).Count() : 0, "file")}";

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    public override string ToString() => Name;
}

/// <summary>New Template: name (the id follows it until you change the id), kind, library, language.</summary>
public sealed partial class NewTemplateDialogViewModel : DialogViewModelBase
{
    private bool _idEdited;
    private bool _settingId;

    public NewTemplateDialogViewModel(IReadOnlyList<TemplateLibrary> libraries)
    {
        Libraries = libraries;
        _library = libraries.FirstOrDefault();
    }

    public override string Title => "New Template";
    public override string OkText => "Create";
    public override bool FitsContent => true;
    public override double Width => 480;
    public override double MinHeight => 0;

    public IReadOnlyList<TemplateLibrary> Libraries { get; }
    public bool HasSeveralLibraries => Libraries.Count > 1;
    public IReadOnlyList<string> Languages => NewTemplate.KnownLanguages;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _id = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsItem))]
    private bool _isProject = true;

    public bool IsItem
    {
        get => !IsProject;
        set => IsProject = !value;
    }

    [ObservableProperty] private TemplateLibrary? _library;
    [ObservableProperty] private string _language = "Any";
    [ObservableProperty] private string? _error;

    partial void OnNameChanged(string value)
    {
        Error = null;
        if (_idEdited || Library is null)
            return;
        _settingId = true;
        Id = TemplateIds.Unique(Library, TemplateIds.FromName(value));
        _settingId = false;
    }

    partial void OnIdChanged(string value)
    {
        Error = null;
        if (!_settingId)
            _idEdited = true;
    }

    protected override Task<bool> ApplyChangesAsync()
    {
        if (Library is null)
            Error = "There's no writable library.";
        else if (string.IsNullOrWhiteSpace(Name))
            Error = "Enter a name.";
        else if (ManifestValidator.Validate(NewTemplate.Create(Name, Id.Trim(), TemplateKind.Project, Language))
                     .FirstOrDefault(i => i.Field == "id") is { } idIssue)
            Error = idIssue.Message;
        else if (Directory.Exists(Path.Combine(Library.Root, Id.Trim())))
            Error = $"{Library.Name} already has a template \"{Id.Trim()}\".";
        else
            Error = null;
        return Task.FromResult(Error is null);
    }
}
