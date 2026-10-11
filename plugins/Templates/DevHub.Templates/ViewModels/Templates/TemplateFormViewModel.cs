using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Services;
using DevHub.Templates.Core;
using DevHub.Templates.Core.Generation;
using DevHub.Templates.Core.Rendering;

namespace DevHub.ViewModels.Templates;

/// <summary>
/// Step 1 of New Project: what the template is, its variables as a form, where the project goes,
/// and "Raw". The folder the values make is shown live; problems with the values show under their
/// fields once Next was pressed (not while you're still typing the first time).
/// </summary>
public sealed partial class TemplateFormViewModel : ViewModelBase, IDisposable
{
    private readonly TemplatesService _templates;
    private readonly IDialogService _dialogs;
    private readonly Action<TemplateFormViewModel> _next;
    private bool _showErrors;

    /// <param name="previous">Values typed earlier (another template's form): fields with the same name start with them.</param>
    /// <param name="next">Called by Next when everything is valid.</param>
    public TemplateFormViewModel(TemplateEntry template, string location, IReadOnlyDictionary<string, string> previous,
        TemplatesService templates, IDialogService dialogs, Action<TemplateFormViewModel> next)
    {
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
        Update();
    }

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

    public ObservableCollection<VariableFieldViewModel> Fields { get; } = [];
    public bool HasFields => Fields.Count > 0;

    /// <summary>The folder the project folder is created in.</summary>
    [ObservableProperty] private string _location;

    /// <summary>Only the base command (no content, steps or post-create).</summary>
    [ObservableProperty] private bool _raw;

    public bool CanBeRaw => IsProject && Manifest!.BaseCommand is not null;

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

    partial void OnLocationChanged(string value) => Update();

    private void OnFieldChanged(object? sender, EventArgs e) => Update();

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _dialogs.PickFolderAsync("Create the project in") is { } folder)
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
        return IsProject && TargetPath is not null && Fields.All(f => f.Error is null);
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
            TargetText = "Fill in the fields to see where the project goes.";
            TargetIsError = false;
            return;
        }
        if (manifest.Kind != TemplateKind.Project)
            return;

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
