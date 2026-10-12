using CommunityToolkit.Mvvm.ComponentModel;
using DevHub.Controls;
using DevHub.Integration;
using DevHub.Services;
using DevHub.Templates.Core;
using DevHub.Templates.Core.Commands;
using DevHub.Templates.Core.Generation;
using DevHub.Templates.Core.Rendering;
using DevHub.ViewModels.Templates;

namespace DevHub.ViewModels.Documents;

/// <summary>
/// Add Files from Template: an item template applied to a folder you already have (§7), one page at
/// a time like New Project, without its Commands step (an item has no base command; the post-create
/// commands are listed in the preview, before Apply):
/// <list type="number">
/// <item>Details: the template, the folder, its variables (Namespace pre-filled from the folder).</item>
/// <item>Preview: what's added and moved, files already there (overwrite or keep), your files a step
///       deletes; editable like New Project's, Save to Template included. Apply writes it.</item>
/// <item>Done: after the post-create commands (live output).</item>
/// </list>
/// Opened from the File menu, from Projects (right-click a project) and repository tabs (through
/// <see cref="IAddFilesFromTemplate"/>, with the folder filled in), and from the Templates tab.
/// </summary>
public sealed partial class AddFilesViewModel : DocumentViewModelBase
{
    private readonly TemplatesService _templates;
    private readonly IDialogService _dialogs;
    private readonly IStatusService _status;

    private TemplateEntry? _template;
    private TemplateFormViewModel? _form;
    private RenderContext? _context;
    private StagedItem? _staged;
    private string _folder = "";

    public AddFilesViewModel(TemplatesService templates, IDialogService dialogs, IStatusService status)
    {
        _templates = templates;
        _dialogs = dialogs;
        _status = status;
        Browser = new TemplateBrowserViewModel(templates, OnTemplateSelected, kind: TemplateBrowserViewModel.ItemKind);
    }

    public override string Title => "Add Files";
    public override string Icon => IconKind.Template;
    public override bool FillsEditorArea => true;
    public override DocumentNavigation Navigation => new() { Text = "Add Files from Template…", Menu = "File", MenuOrder = 1, WelcomeOrder = 15 };

    public TemplateBrowserViewModel Browser { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPage))]
    private ViewModelBase? _page;

    public bool HasPage => Page is not null;

    [ObservableProperty] private IReadOnlyList<StepIndicatorViewModel> _steps = StepIndicatorViewModel.For(0, StepIndicatorViewModel.ItemSteps);

    /// <summary>The folder the files go into (shown above the list; the form starts with it).</summary>
    public string FolderText => _folder.Length == 0 ? "Pick a template, then the folder." : "Into " + _folder;

    protected override void OnActivationChanged(bool isActive)
    {
        if (isActive)
            _ = Browser.ReloadIfChangedAsync();
    }

    /// <summary>
    /// From Projects / a repository tab: add files to this folder. Ignored in the middle of an apply
    /// (the tab just comes forward); otherwise the form switches to the folder.
    /// </summary>
    public void SetFolder(string folder)
    {
        if (!Browser.IsEnabled)
            return;
        _folder = folder;
        OnPropertyChanged(nameof(FolderText));
        if (_form is not null)
            _form.Location = folder;
        else if (Page is DonePageViewModel)
            ShowForm(_template);
    }

    /// <summary>From the Templates tab: this item template (call before opening this tab).</summary>
    public async Task SelectTemplateAsync(string reference)
    {
        await Browser.ReloadIfChangedAsync();
        if (Browser.IsEnabled)
            Browser.Select(reference);
    }

    private void OnTemplateSelected(TemplateEntry? template)
    {
        if (Page is not (null or TemplateFormViewModel or DonePageViewModel))
            return;
        ShowForm(template);
    }

    private void ShowForm(TemplateEntry? template)
    {
        _template = template;
        var entered = _form?.Entered ?? new Dictionary<string, string>();
        var folder = _form?.Location ?? _folder;
        _form?.Dispose();
        _form = template is null ? null : new TemplateFormViewModel(template, folder, entered, _templates, _dialogs, OnFormNext, forItems: true);
        SetPage(_form, 0);
    }

    private void SetPage(ViewModelBase? page, int step)
    {
        Page = page;
        Steps = StepIndicatorViewModel.For(step, StepIndicatorViewModel.ItemSteps);
        Browser.IsEnabled = page is null or TemplateFormViewModel or DonePageViewModel;
    }

    private void OnFormNext(TemplateFormViewModel form) => _ = StageAsync(form);

    private void BackToForm()
    {
        _staged?.Dispose();
        _staged = null;
        SetPage(_form, 0);
    }

    private async Task StageAsync(TemplateFormViewModel form)
    {
        var template = _template!;
        _folder = form.TargetPath!;
        OnPropertyChanged(nameof(FolderText));
        _context = _templates.NewContext(form.Values);
        var context = _context;
        var folder = _folder;

        StageResult<StagedItem> result;
        try
        {
            // Reading the folder and rendering the template's files: quick, no commands run here.
            result = await Task.Run(() => ItemApplier.StageAsync(template, context, folder));
        }
        catch (Exception ex)
        {
            result = new StageResult<StagedItem>(null, [ex.Message]);
        }
        if (result.Staged is not { } staged)
        {
            _status.Show($"Couldn't prepare the files: {string.Join(" ", result.Errors)}");
            await _dialogs.ShowDialogAsync(new Dialogs.ConfirmDialogViewModel("Can't Add the Files",
                "Nothing was changed in the folder.", "OK", result.Errors, destructive: false, showCancel: false));
            return;
        }

        _staged = staged;
        var commands = TemplateRendering.PostCreate(template.Manifest!, context);
        SetPage(new PreviewPageViewModel(staged, null, raw: false, template.Library.IsReadOnly, _dialogs,
            SaveEditsAsync, ApplyAsync, BackToForm, commands), 1);
    }

    private async Task<SaveOutcome> SaveEditsAsync(IReadOnlyList<TemplateStep> edits)
    {
        var (saved, outcome) = await TemplateFlow.SaveEditsAsync(_templates, _template!, _form!.Values, edits, "time");
        if (saved is not null)
        {
            _template = saved;
            await Browser.ReloadAsync(select: saved.Reference);
        }
        return outcome;
    }

    private async Task ApplyAsync()
    {
        var staged = _staged!;
        var preview = (PreviewPageViewModel)Page!;
        var overwrite = preview.Overwrite;
        PlaceResult applied;
        try
        {
            applied = await Task.Run(() => staged.ApplyAsync(overwrite));
        }
        catch (Exception ex)
        {
            preview.ShowError("Couldn't add the files: " + ex.Message + " Some changes may already be in the folder.");
            return;
        }
        _staged = null;
        var folder = applied.Path!;

        var commands = TemplateRendering.PostCreate(_template!.Manifest!, _context!);
        var log = new OutputLogViewModel();
        IReadOnlyList<CommandResult> results = [];
        var cancelled = false;
        if (commands.Count > 0)
            (results, cancelled) = await TemplateFlow.RunPostCreateAsync(_templates, _status, commands, folder, log, run => SetPage(run, 2));

        _status.Show(cancelled || results.Any(r => !r.Succeeded) ? $"Added files to {folder}; a command didn't finish." : $"Added files to {folder}.");
        SetPage(new DonePageViewModel(folder, results, cancelled, log, null,
            () => TemplatesService.Reveal(folder),
            () => ShowForm(_template), isItem: true), 2);
    }
}

/// <summary>
/// Templates' <see cref="IAddFilesFromTemplate"/>: brings the Add Files tab forward with the folder
/// filled in. The tab manager is looked up when it's used, not injected: it's built from the
/// documents, and Projects / Git take this service, so injecting it would be a startup cycle.
/// </summary>
public sealed class AddFilesOpener(AddFilesViewModel addFiles, IServiceProvider services) : IAddFilesFromTemplate
{
    public void Open(string folder)
    {
        addFiles.SetFolder(folder);
        (services.GetService(typeof(IDocumentService)) as IDocumentService)?.OpenById(addFiles.Id);
    }
}
