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
/// New Project: the template list on the left, the generation on the right, one page at a time
/// (docs/TEMPLATE_FORMAT.md §4):
/// <list type="number">
/// <item>Details: the template and its form (<see cref="TemplateFormViewModel"/>).</item>
/// <item>Commands: everything that will run, rendered; nothing runs before Run (<see cref="CommandsPageViewModel"/>).</item>
/// <item>Staging (<see cref="RunPageViewModel"/>), then the editable Preview (<see cref="PreviewPageViewModel"/>).</item>
/// <item>Create: placed, post-create commands with live output, then Done (<see cref="DonePageViewModel"/>).</item>
/// </list>
/// A singleton tab: switching tabs keeps the page (and a running generation) as it is.
/// </summary>
public sealed partial class GenerateViewModel : DocumentViewModelBase
{
    private readonly TemplatesService _templates;
    private readonly IDialogService _dialogs;
    private readonly IStatusService _status;
    private readonly Func<IProjectFolders?> _projectFolders;
    private readonly Func<IProjectOpener?> _opener;

    // The generation in progress.
    private TemplateEntry? _template;
    private TemplateFormViewModel? _form;
    private RenderContext? _context;
    private bool _raw;
    private StagedProject? _staged;

    /// <param name="projectFolders">From the Projects plugin: the default location, and a rescan after creating. A factory for the same reason as <paramref name="opener"/> (the Projects tool window takes the opener).</param>
    /// <param name="opener">
    /// Git's opener when Git is on ("Open in DevHub" opens the new project as a repository tab). A factory,
    /// asked only when Done needs it: the opener needs the DocumentManager, which is built from the
    /// documents (this one too), so taking it in the constructor is a cycle that hangs startup.
    /// </param>
    public GenerateViewModel(TemplatesService templates, IDialogService dialogs, IStatusService status,
        Func<IProjectFolders?> projectFolders, Func<IProjectOpener?> opener)
    {
        _templates = templates;
        _dialogs = dialogs;
        _status = status;
        _projectFolders = projectFolders;
        _opener = opener;
        Browser = new TemplateBrowserViewModel(templates, OnTemplateSelected);
    }

    public override string Title => "New Project";
    public override string Icon => IconKind.Add;
    public override bool FillsEditorArea => true;
    public override DocumentNavigation Navigation => new() { Text = "New Project…", Gesture = "Ctrl+N", Menu = "File", MenuOrder = 0, WelcomeOrder = 10 };

    public TemplateBrowserViewModel Browser { get; }

    /// <summary>The page on the right (a form, the commands, the output, the preview, done), or null: nothing selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPage))]
    private ViewModelBase? _page;

    public bool HasPage => Page is not null;

    [ObservableProperty] private IReadOnlyList<StepIndicatorViewModel> _steps = StepIndicatorViewModel.For(0);

    protected override void OnActivationChanged(bool isActive)
    {
        // The first time, and again after templates changed elsewhere (the Templates tab, an editor).
        if (isActive)
            _ = Browser.ReloadIfChangedAsync();
    }

    /// <summary>"Use in New Project" from the Templates tab: selects that template (call before opening this tab).</summary>
    public async Task SelectTemplateAsync(string reference)
    {
        await Browser.ReloadIfChangedAsync();
        if (Browser.IsEnabled) // not in the middle of a generation
            Browser.Select(reference);
    }

    /// <summary>A template was picked (or the same one read again): its form, keeping what was typed.</summary>
    private void OnTemplateSelected(TemplateEntry? template)
    {
        // Mid-generation the list is disabled; a reload (e.g. after Save to Template) doesn't interrupt.
        if (Page is not (null or TemplateFormViewModel or DonePageViewModel))
            return;
        ShowForm(template);
    }

    private void ShowForm(TemplateEntry? template)
    {
        _template = template;
        var entered = _form?.Entered ?? new Dictionary<string, string>();
        var location = _form?.Location ?? DefaultLocation();
        _form?.Dispose();
        _form = template is null ? null : new TemplateFormViewModel(template, location, entered, _templates, _dialogs, OnFormNext);
        SetPage(_form, 0);
    }

    private string DefaultLocation() =>
        _templates.LastLocation is { } last && Directory.Exists(last) ? last
        : _projectFolders()?.Folders.FirstOrDefault(Directory.Exists)
          ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private void SetPage(ViewModelBase? page, int step)
    {
        Page = page;
        Steps = StepIndicatorViewModel.For(step);
        Browser.IsEnabled = page is null or TemplateFormViewModel or DonePageViewModel;
    }

    // ── 2. Commands ─────────────────────────────────────────────────────────────

    private void OnFormNext(TemplateFormViewModel form)
    {
        var manifest = form.Manifest!;
        _raw = form.Raw && form.CanBeRaw;
        _context = _templates.NewContext(form.Values);
        var commands = TemplateRendering.AllCommands(manifest, _context, _raw);
        if (commands.Count == 0)
        {
            _ = StageAsync(); // nothing will run: straight to the files
            return;
        }
        SetPage(new CommandsPageViewModel(form.Name, form.TargetPath!, _raw, commands, BackToForm, () => _ = StageAsync()), 1);
    }

    private void BackToForm()
    {
        _staged?.Dispose();
        _staged = null;
        SetPage(_form, 0);
    }

    // ── 3. Staging → preview ────────────────────────────────────────────────────

    private async Task StageAsync()
    {
        var template = _template!;
        var form = _form!;
        var name = Path.GetFileName(form.TargetPath!);
        var log = new OutputLogViewModel();
        var cts = new CancellationTokenSource();
        var run = new RunPageViewModel($"Creating {name}", log, cts.Cancel, BackToForm);
        SetPage(run, 1);

        var task = _status.StartTask($"Creating {name}…", cts.Cancel);
        var progress = new Progress<GenerationProgress>(p =>
        {
            if (p.Output is { } line)
                log.Add(line);
            else
            {
                run.Step = p.Step;
                log.Add("▶ " + p.Step, isHeader: true);
                task.Report(p.Step, null);
            }
        });

        StageResult<StagedProject> result;
        var context = _context!;
        var parent = form.Location.Trim();
        var raw = _raw;
        var ct = cts.Token;
        try
        {
            result = await Task.Run(() => _templates.Generator.StageAsync(template, context, parent, raw, progress, ct), ct);
        }
        catch (OperationCanceledException)
        {
            _status.Show($"Cancelled; nothing was written to {parent}.");
            SetPage(_form, 0);
            return;
        }
        catch (Exception ex)
        {
            // The generator cleans up its staging folder before passing an error on; show it instead of a stuck page.
            result = new StageResult<StagedProject>(null, [ex.Message]);
        }
        finally
        {
            run.IsRunning = false; // Cancel off before its token source goes
            task.Dispose();
            cts.Dispose();
        }

        if (result.Staged is not { } staged)
        {
            run.Error = string.Join(Environment.NewLine, result.Errors) + Environment.NewLine + $"Nothing was written to {parent}.";
            _status.Show($"Couldn't create {name}.");
            return;
        }

        _staged = staged;
        SetPage(new PreviewPageViewModel(staged, template.Manifest!.BaseCommand?.Command.TrimStart('@'), raw,
            template.Library.IsReadOnly, _dialogs, SaveEditsAsync, CreateAsync, BackToForm), 2);
    }

    /// <summary>Save to Template: the preview's edits become steps (in a personal copy when the template is read-only).</summary>
    private async Task<SaveOutcome> SaveEditsAsync(IReadOnlyList<TemplateStep> edits)
    {
        var (saved, outcome) = await TemplateFlow.SaveEditsAsync(_templates, _template!, _form!.Values, edits, "project");
        if (saved is not null)
        {
            // From now on this generation belongs to the saved template (a second save adds to it, not to another copy).
            _template = saved;
            await Browser.ReloadAsync(select: saved.Reference);
        }
        return outcome;
    }

    // ── 4. Place → post-create → done ───────────────────────────────────────────

    private async Task CreateAsync()
    {
        var staged = _staged!;
        var preview = (PreviewPageViewModel)Page!;
        PlaceResult placed;
        try
        {
            placed = await Task.Run(() => staged.PlaceAsync());
        }
        catch (Exception ex)
        {
            placed = new PlaceResult(null, "Couldn't create the project: " + ex.Message);
        }
        if (placed.Path is not { } path)
        {
            preview.ShowError(placed.Error!);
            return;
        }
        _staged = null;
        _templates.LastLocation = staged.ParentFolder;

        var commands = _raw ? [] : TemplateRendering.PostCreate(_template!.Manifest!, _context!);
        var log = new OutputLogViewModel();
        IReadOnlyList<CommandResult> results = [];
        var cancelled = false;
        if (commands.Count > 0)
            (results, cancelled) = await TemplateFlow.RunPostCreateAsync(_templates, _status, commands, path, log, run => SetPage(run, 3));

        await RescanProjectsAsync(staged.ParentFolder);
        _status.Show(cancelled || results.Any(r => !r.Succeeded) ? $"Created {path}; a command didn't finish." : $"Created {path}.");
        SetPage(new DonePageViewModel(path, results, cancelled, log,
            _opener() is { } projectOpener ? () => projectOpener.Open(path) : null,
            () => TemplatesService.Reveal(path),
            () => ShowForm(_template)), 3);
    }

    /// <summary>The new project shows up in the Projects tool window when it's in one of its folders.</summary>
    private async Task RescanProjectsAsync(string parent)
    {
        if (_projectFolders() is not { } projects)
            return;
        var full = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
        if (projects.Folders.Any(f => full.StartsWith(Path.GetFullPath(f).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            await projects.RescanAsync();
    }
}
