using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Controls;
using DevHub.Services;
using DevHub.Templates.Core;
using DevHub.Templates.Core.Editing;
using DevHub.Templates.Core.Importing;

namespace DevHub.ViewModels.Documents;

/// <summary>
/// Import Folder (§10): an existing folder becomes a template. Left: the folder, what to leave out
/// (ignore patterns), which literals become variables ("LumberjackHollow" → Name), and the new
/// template's name, id, kind, language, categories and library. Right: the preview (ignored items
/// greyed out, placeholders per file, what each file is called in the template). Create Template
/// writes it and opens it in the editor.
/// </summary>
public sealed partial class ImportViewModel : DocumentViewModelBase
{
    private readonly TemplatesService _templates;
    private readonly IDialogService _dialogs;
    private readonly IStatusService _status;
    private bool _idEdited;
    private bool _settingId;

    public ImportViewModel(TemplatesService templates, IDialogService dialogs, IStatusService status)
    {
        _templates = templates;
        _dialogs = dialogs;
        _status = status;
        Libraries = templates.Libraries.Where(l => !l.IsReadOnly).ToList();
        _library = Libraries.FirstOrDefault();
    }

    public override string Title => "Import";
    public override string Icon => IconKind.Import;
    public override bool FillsEditorArea => true;
    public override DocumentNavigation Navigation => new() { Text = "Import Folder as Template…", Menu = "File", MenuOrder = 2, WelcomeOrder = 20 };

    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private string _ignores = string.Join(Environment.NewLine, IgnoreRules.Defaults);

    public ObservableCollection<ReplacementRowViewModel> Replacements { get; } = [];

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

    [ObservableProperty] private string _language = "Any";
    [ObservableProperty] private string _categories = "";
    public IReadOnlyList<string> Languages => NewTemplate.KnownLanguages;

    public IReadOnlyList<TemplateLibrary> Libraries { get; }
    public bool HasSeveralLibraries => Libraries.Count > 1;
    [ObservableProperty] private TemplateLibrary? _library;

    public ObservableCollection<ImportNodeViewModel> Nodes { get; } = [];
    [ObservableProperty] private string _summary = "Choose a folder, then Preview.";
    [ObservableProperty] private IReadOnlyList<string> _problems = [];
    public bool HasProblems => Problems.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(CreateCommand))]
    private bool _isBusy;

    partial void OnProblemsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasProblems));

    /// <summary>A new folder: its name suggests the template's name and the literal that becomes Name.</summary>
    partial void OnFolderChanged(string value)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(value.Trim()));
        if (name.Length == 0)
            return;
        if (Name.Length == 0)
            Name = name;
        if (Replacements.Count == 0)
            AddReplacement(name, "Name");
    }

    partial void OnNameChanged(string value)
    {
        if (_idEdited || Library is null)
            return;
        _settingId = true;
        Id = TemplateIds.Unique(Library, TemplateIds.FromName(value));
        _settingId = false;
    }

    partial void OnIdChanged(string value)
    {
        if (!_settingId)
            _idEdited = true;
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _dialogs.PickFolderAsync("Folder to make a template from") is { } folder)
            Folder = folder;
    }

    [RelayCommand]
    private void AddReplacementRow() => AddReplacement("", "Name");

    private void AddReplacement(string literal, string variable) =>
        Replacements.Add(new ReplacementRowViewModel(literal, variable, row => Replacements.Remove(row)));

    private bool CanAct() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task PreviewAsync()
    {
        if (!FolderExists(out var folder))
            return;
        IsBusy = true;
        try
        {
            var (ignore, replacer) = Rules();
            var preview = await Task.Run(() => TemplateImporter.PreviewAsync(folder, ignore, replacer));
            Show(preview);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problems = ["Couldn't read the folder: " + ex.Message];
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task CreateAsync()
    {
        if (!FolderExists(out var folder))
            return;
        if (Library is null)
        {
            Problems = ["There's no writable library to put the template in."];
            return;
        }
        var manifest = BuildManifest();
        if (ManifestValidator.Validate(manifest) is { Count: > 0 } issues)
        {
            Problems = issues.Select(i => i.ToString()).ToList();
            return;
        }

        IsBusy = true;
        try
        {
            var (ignore, replacer) = Rules();
            var library = Library;
            var result = await Task.Run(() => TemplateImporter.ImportAsync(folder, ignore, replacer, library, manifest));
            if (result.Entry is not { } entry)
            {
                Problems = result.Errors;
                return;
            }
            Problems = [];
            _templates.NotifyChanged();
            _status.Show($"Created the template {entry.Reference} from {folder}.");
            OpenDocument?.Invoke(TemplateEditorViewModel.IdFor(entry.Folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problems = ["Couldn't create the template: " + ex.Message];
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The new template: a variable for each replacement (Name first), required.</summary>
    private TemplateManifest BuildManifest()
    {
        var manifest = NewTemplate.Create(Name, Id.Trim(), IsProject ? TemplateKind.Project : TemplateKind.Item, Language);
        var extra = Replacements.Select(r => r.Variable.Trim())
            .Where(v => v.Length > 0 && v != "Name")
            .Distinct(StringComparer.Ordinal)
            .Select(v => new TemplateVariable { Name = v, Required = true });
        return manifest with
        {
            Categories = ListText.SplitList(Categories),
            Variables = [.. manifest.Variables, .. extra],
        };
    }

    private (IgnoreRules, LiteralReplacer) Rules() =>
        (new IgnoreRules(ListText.SplitLines(Ignores)),
         new LiteralReplacer(Replacements.Select(r => new LiteralReplacement(r.Literal.Trim(), r.Variable.Trim()))));

    private bool FolderExists(out string folder)
    {
        folder = Folder.Trim();
        if (Path.IsPathFullyQualified(folder) && Directory.Exists(folder))
            return true;
        Problems = ["Choose an existing folder to import."];
        return false;
    }

    private void Show(ImportPreview preview)
    {
        var byPath = new Dictionary<string, ImportNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<ImportNodeViewModel>();
        foreach (var entry in preview.Entries) // a folder comes before what's in it
        {
            var node = new ImportNodeViewModel(entry);
            byPath[entry.Path] = node;
            if (entry.Parent.Length > 0 && byPath.TryGetValue(entry.Parent, out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }
        Nodes.Clear();
        foreach (var root in roots)
            Nodes.Add(root);
        Summary = $"{preview.FileCount} files go into the template, {preview.IgnoredCount} items left out, {preview.Hits} placeholders put in.";
        Problems = preview.Problems;
    }
}

/// <summary>One literal → variable row ("LumberjackHollow" → Name).</summary>
public sealed partial class ReplacementRowViewModel : ViewModelBase
{
    public ReplacementRowViewModel(string literal, string variable, Action<ReplacementRowViewModel> remove)
    {
        _literal = literal;
        _variable = variable;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    [ObservableProperty] private string _literal;
    [ObservableProperty] private string _variable;

    public RelayCommand RemoveCommand { get; }

    public override string ToString() => $"{Literal} → {Variable}";
}

/// <summary>A file or folder in the import preview.</summary>
public sealed class ImportNodeViewModel(ImportEntry entry) : ViewModelBase
{
    public ImportEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public bool IsFolder => Entry.IsFolder;
    public bool IsIgnored => Entry.IsIgnored;
    public bool HasHits => Entry.Hits > 0;
    public string HitsText => Entry.Hits == 1 ? "1 placeholder" : $"{Entry.Hits} placeholders";

    /// <summary>Its name in the template when that differs ("{{Name}}.csproj").</summary>
    public string? TargetName => Entry.TargetPath is { } target && target[(target.LastIndexOf('/') + 1)..] is var name && name != Name ? "→ " + name : null;
    public bool HasTargetName => TargetName is not null;

    public System.Collections.ObjectModel.ObservableCollection<ImportNodeViewModel> Children { get; } = [];

    public override string ToString() => IsIgnored ? $"{Name} (left out)" : Name;
}
