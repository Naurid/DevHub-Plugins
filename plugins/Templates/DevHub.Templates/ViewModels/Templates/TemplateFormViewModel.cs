using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Services;
using DevHub.Templates.Core;
using DevHub.Templates.Core.Generation;
using DevHub.Templates.Core.Items;
using DevHub.Templates.Core.Rendering;

namespace DevHub.ViewModels.Templates;

/// <summary>
/// Step 1 of New Project (and of Add Files, for item templates): what the template is, its variables
/// as a form, where the project goes (or which folder the files go into), and "Raw" (projects). The
/// folder is shown live; problems with the values show under their fields once Next was pressed (not
/// while you're still typing the first time). For an item template a variable called Namespace starts
/// with the folder's namespace (nearest .csproj, .asmdef or Unity project) and follows the folder
/// until you type your own.
/// </summary>
public sealed partial class TemplateFormViewModel : ViewModelBase, IDisposable
{
    private readonly TemplatesService _templates;
    private readonly IDialogService _dialogs;
    private readonly Action<TemplateFormViewModel> _next;
    private bool _showErrors;
    private bool _settingNamespace;
    private bool _namespaceTyped;

    /// <param name="previous">Values typed earlier (another template's form): fields with the same name start with them.</param>
    /// <param name="next">Called by Next when everything is valid.</param>
    /// <param name="forItems">Add Files (item templates, an existing folder) instead of New Project.</param>
    public TemplateFormViewModel(TemplateEntry template, string location, IReadOnlyDictionary<string, string> previous,
        TemplatesService templates, IDialogService dialogs, Action<TemplateFormViewModel> next, bool forItems = false)
    {
        ForItems = forItems;
        Template = template;
        _templates = templates;
        _dialogs = dialogs;
        _next = next;
        _location = location;

        foreach (var variable in Manifest?.Variables ?? [])
        {
            var field = new VariableFieldViewModel(variable, previous.GetValueOrDefault(variable.Name));
            field.Changed += OnFieldChanged;
            Fields.Add(field);
        }
        if (ShowsForm && ForItems)
            GuessNamespace();
        Update();
    }

    public bool ForItems { get; }

    public TemplateEntry Template { get; }
    public TemplateManifest? Manifest => Template.Manifest;

    public string Name => Manifest?.Name is { Length: > 0 } name ? name : Template.Id;
    public string? Description => Manifest?.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string Language => Manifest?.Language ?? "";
    public IReadOnlyList<string> Categories => Manifest?.Categories ?? [];
    public string LibraryText => Template.Library.IsReadOnly ? $"{Template.Library.Name} (read-only)" : Template.Library.Name;

    public bool IsBroken => !Template.IsUsable;
    public IReadOnlyList<string> Problems => Template.Problems;
    public bool IsItem => !IsBroken && Manifest!.Kind == TemplateKind.Item;
    public bool IsProject => !IsBroken && Manifest!.Kind == TemplateKind.Project;

    /// <summary>The template fits this flow (a project template in New Project, an item template in Add Files).</summary>
    public bool ShowsForm => !IsBroken && IsItem == ForItems;

    public bool IsWrongKind => !IsBroken && !ShowsForm;

    public string WrongKindText => IsItem
        ? "This is an item template: it adds files to a folder you already have. Use it from Add Files from Template (File menu, a project's right-click menu in Projects, or a repository tab)."
        : "This is a project template: it creates a new project folder. Use it from New Project (Ctrl+N).";

    public string LocationLabel => ForItems ? "Folder" : "Location";
    public string LocationDescription => ForItems ? "The files are added in here (it must exist)." : "The project folder is created in here.";

    public ObservableCollection<VariableFieldViewModel> Fields { get; } = [];
    public bool HasFields => Fields.Count > 0;

    /// <summary>The folder the project folder is created in.</summary>
    [ObservableProperty] private string _location;

    /// <summary>Only the base command (no content, steps or post-create).</summary>
    [ObservableProperty] private bool _raw;

    public bool CanBeRaw => IsProject && !ForItems && Manifest!.BaseCommand is not null;

    public string RawText => Manifest?.BaseCommand is { } command
        ? $"Raw project: only run {command.Command} (no template files, steps or post-create commands)"
        : "";

    /// <summary>"Creates C:\Dev\MyGame", or why that folder can't be used.</summary>
    [ObservableProperty] private string _targetText = "";
    [ObservableProperty] private bool _targetIsError;

    /// <summary>The values as checked (defaults filled in) and the full project folder; valid after <see cref="Validate"/>.</summary>
    public IReadOnlyDictionary<string, string> Values { get; private set; } = new Dictionary<string, string>();
    public string? TargetPath { get; private set; }

    /// <summary>What the user typed, by variable name (carried to the next template's form).</summary>
    public IReadOnlyDictionary<string, string> Entered => Fields.ToDictionary(f => f.Variable.Name, f => f.Value);

    /// <summary>Stops listening to the fields (the form is replaced).</summary>
    public void Dispose()
    {
        foreach (var field in Fields)
            field.Changed -= OnFieldChanged;
    }

    /// <summary>The location was changed here (typed or Browse), not just the template's default.</summary>
    public bool LocationEdited { get; private set; }

    partial void OnLocationChanged(string value)
    {
        LocationEdited = true;
        if (ForItems && !_namespaceTyped)
            GuessNamespace();
        Update();
    }

    private void OnFieldChanged(object? sender, EventArgs e)
    {
        if (!_settingNamespace && sender is VariableFieldViewModel { Variable.Name: NamespaceInference.VariableName })
            _namespaceTyped = true;
        Update();
    }

    /// <summary>Fills the Namespace field from the folder; the hint under it says where the value came from.</summary>
    private void GuessNamespace()
    {
        if (Fields.FirstOrDefault(f => f.Variable.Name == NamespaceInference.VariableName && f.IsText) is not { } field)
            return;
        var folder = Location.Trim();
        var guess = Path.IsPathFullyQualified(folder) ? NamespaceInference.Infer(folder) : null;
        _settingNamespace = true;
        if (guess is not null)
            field.Text = guess.Namespace;
        field.Hint = guess is null
            ? "No .csproj, .asmdef or Unity project above this folder: type the namespace."
            : $"Filled in {guess.Source}; change it if you like.";
        _settingNamespace = false;
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _dialogs.PickFolderAsync(ForItems ? "Add the files to" : "Create the project in") is { } folder)
            Location = folder;
    }

    [RelayCommand]
    private void Next()
    {
        _showErrors = true;
        if (Validate())
            _next(this);
    }

    /// <summary>Checks the values and the target folder; shows the problems. True when Next can go on.</summary>
    public bool Validate()
    {
        Update();
        return ShowsForm && TargetPath is not null && Fields.All(f => f.Error is null);
    }

    /// <summary>Re-checks after every change: the target line always, the field errors once Next was pressed.</summary>
    private void Update()
    {
        if (Manifest is not { } manifest)
            return;

        var check = VariableValues.Check(manifest, Entered);
        Values = check.Values;
        foreach (var field in Fields)
            field.Error = _showErrors ? check.Issues.FirstOrDefault(i => i.Variable == field.Variable.Name)?.Message : null;
        if (!check.Succeeded && !_showErrors)
        {
            // Not filled in yet: say what's missing calmly instead of showing a path built from empty values.
            TargetPath = null;
            TargetText = ForItems ? "Fill in the fields." : "Fill in the fields to see where the project goes.";
            TargetIsError = false;
            return;
        }
        if (!ShowsForm)
            return;
        if (ForItems)
        {
            var folder = Location.Trim();
            var exists = Path.IsPathFullyQualified(folder) && Directory.Exists(folder);
            TargetPath = exists && check.Succeeded ? Path.GetFullPath(folder) : null;
            TargetText = exists ? "Adds to " + Path.GetFullPath(folder)
                : folder.Length == 0 || !Path.IsPathFullyQualified(folder) ? "Choose the folder to add the files to."
                : $"The folder {folder} doesn't exist.";
            TargetIsError = !exists;
            return;
        }

        var target = ProjectGenerator.TargetFor(manifest, _templates.NewContext(Values), Location.Trim());
        TargetPath = target.Succeeded && check.Succeeded ? Path.Combine(Location.Trim(), target.Path!) : null;
        TargetText = target.Succeeded ? "Creates " + Path.Combine(Location.Trim(), target.Path!) : target.Error!;
        TargetIsError = !target.Succeeded;
    }
}

/// <summary>One variable in the form: a text box (string), a check box (bool) or a drop-down (choice).</summary>
public sealed partial class VariableFieldViewModel : ViewModelBase
{
    public VariableFieldViewModel(TemplateVariable variable, string? previous)
    {
        Variable = variable;
        var initial = previous ?? variable.DefaultText;
        _text = initial;
        _isChecked = initial == "true";
        _selectedChoice = variable.Choices.Contains(initial) ? initial : variable.Choices.FirstOrDefault();
    }

    public TemplateVariable Variable { get; }

    public string Label => (Variable.Label ?? Variable.Name) + (Variable.Required && !IsBool ? " *" : "");
    public bool IsText => Variable.Type == VariableType.String;
    public bool IsBool => Variable.Type == VariableType.Bool;
    public bool IsChoice => Variable.Type == VariableType.Choice;
    public IReadOnlyList<string> Choices => Variable.Choices;

    /// <summary>The placeholder it fills, for the tooltip ("{{Name}}").</summary>
    public string ToolTip => "{{" + Variable.Name + "}}" + (Variable.Pattern is { } p ? $"  ·  must match {p}" : "");

    [ObservableProperty] private string _text;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string? _selectedChoice;
    [ObservableProperty] private string? _error;

    /// <summary>A note under the field (where a pre-filled Namespace came from), or null.</summary>
    [ObservableProperty] private string? _hint;

    public string Value => Variable.Type switch
    {
        VariableType.Bool => IsChecked ? "true" : "false",
        VariableType.Choice => SelectedChoice ?? "",
        _ => Text,
    };

    public event EventHandler? Changed;

    partial void OnTextChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);
    partial void OnIsCheckedChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);
    partial void OnSelectedChoiceChanged(string? value) => Changed?.Invoke(this, EventArgs.Empty);

    public override string ToString() => Variable.Label ?? Variable.Name;
}
