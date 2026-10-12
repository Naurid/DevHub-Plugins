using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Services;
using DevHub.Templates.Core;
using DevHub.ViewModels.Documents;
using DevHub.ViewModels.Templates;

namespace DevHub.ViewModels.ToolWindows;

/// <summary>
/// The Templates tool window (left stripe, next to Projects): every template at hand. Double-click
/// (or Use) starts it: New Project for a project template, Add Files for an item template; Edit opens
/// its editor tab. The header has New Project, Add Files, New Template and Reload.
/// </summary>
public sealed partial class TemplatesToolWindowViewModel : ToolWindowViewModelBase
{
    private readonly IDocumentService _documents;
    private readonly GenerateViewModel _generate;
    private readonly AddFilesViewModel _addFiles;
    private readonly TemplatesViewModel _manager;

    /// <param name="documents">To open tabs. Fine to inject here: the tab manager is built from the documents, not the tool windows.</param>
    public TemplatesToolWindowViewModel(TemplatesService templates, IDocumentService documents, GenerateViewModel generate,
        AddFilesViewModel addFiles, TemplatesViewModel manager)
    {
        _documents = documents;
        _generate = generate;
        _addFiles = addFiles;
        _manager = manager;
        Browser = new TemplateBrowserViewModel(templates, OnSelected, Use, TemplateBrowserViewModel.AllKinds);
    }

    public override string Id => "templates";
    public override string Title => "Templates";
    public override string IconKind => Controls.IconKind.Template;
    public override ToolWindowSlot Slot => ToolWindowSlot.Left;
    public override int Order => 10; // after Projects (0): Alt+2
    public override int? WelcomeOrder => 55;

    public TemplateBrowserViewModel Browser { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseSelectedCommand), nameof(EditSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(UseText))]
    private TemplateEntry? _selected;

    /// <summary>"New Project" or "Add Files", by the selected template's kind.</summary>
    public string UseText => Selected?.Manifest?.Kind == TemplateKind.Item ? "Add Files…" : "New Project…";

    /// <summary>Reloads when the panel opens (templates may have changed in the editor meanwhile).</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsOpen) && IsOpen)
            _ = Browser.ReloadIfChangedAsync();
    }

    private void OnSelected(TemplateEntry? template) => Selected = template;

    /// <summary>Starts the template: New Project with it selected, or Add Files for an item template.</summary>
    private async void Use(TemplateEntry template)
    {
        if (template.Manifest?.Kind == TemplateKind.Item)
        {
            await _addFiles.SelectTemplateAsync(template.Reference);
            _documents.OpenById(_addFiles.Id);
        }
        else
        {
            await _generate.SelectTemplateAsync(template.Reference);
            _documents.OpenById(_generate.Id);
        }
    }

    private bool HasUsableSelection() => Selected is { IsUsable: true };
    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasUsableSelection))]
    private void UseSelected()
    {
        if (Selected is { } template)
            Use(template);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void EditSelected()
    {
        if (Selected is { } template)
            _documents.OpenById(TemplateEditorViewModel.IdFor(template.Folder));
    }

    [RelayCommand]
    private void NewProject() => _documents.OpenById(_generate.Id);

    [RelayCommand]
    private void AddFiles() => _documents.OpenById(_addFiles.Id);

    [RelayCommand]
    private async Task NewTemplateAsync()
    {
        await _manager.NewTemplateCommand.ExecuteAsync(null);
        await Browser.ReloadIfChangedAsync();
    }
}
