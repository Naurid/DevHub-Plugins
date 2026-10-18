using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Controls;
using DevHub.Diagrams;
using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.ViewModels.Diagrams;

/// <summary>A palette entry: a shape or a line to draw.</summary>
public sealed partial class PaletteItemViewModel(string name, string description, string type, bool isEdge, Action<PaletteItemViewModel> pick)
    : ObservableObject
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string Type { get; } = type;
    public bool IsEdge { get; } = isEdge;
    public string Icon => IsEdge ? IconKind.ChevronRight : IconKind.Add;

    [ObservableProperty] private bool _isActive;

    [RelayCommand]
    private void Pick() => pick(this);

    public override string ToString() => Name;
}

/// <summary>One editable property in the properties panel; changing it is one Undo step.</summary>
public sealed partial class PropertyFieldViewModel : ObservableObject
{
    private readonly Func<string> _read;
    private readonly Action<string> _write;

    public PropertyFieldViewModel(PropertyField field, Func<string> read, Action<string> write)
    {
        Field = field;
        _read = read;
        _write = write;
        _value = read();
        _choice = field.Choices.FirstOrDefault(c => c.Value == _value) ?? field.Choices.FirstOrDefault();
    }

    public PropertyField Field { get; }
    public string Label => Field.Label;
    public string Hint => Field.Hint;
    public bool IsText => Field.Kind == FieldKind.Text;
    public bool IsMultiline => Field.Kind == FieldKind.MultilineText;
    public bool IsChoice => Field.Kind == FieldKind.Choice;
    public bool IsToggle => Field.Kind == FieldKind.Toggle;
    public bool IsColor => Field.Kind == FieldKind.Color;
    public bool IsNumber => Field.Kind == FieldKind.Number;
    public IReadOnlyList<FieldChoice> Choices => Field.Choices;

    [ObservableProperty] private string _value;
    [ObservableProperty] private FieldChoice? _choice;

    public bool IsOn
    {
        get => Value == "true";
        set
        {
            Value = value ? "true" : "false";
            OnPropertyChanged();
        }
    }

    /// <summary>The colour as a brush-friendly string (for the swatch next to a colour field).</summary>
    public string Swatch => Value.StartsWith('#') && (Value.Length == 7 || Value.Length == 9) ? Value : "#00000000";

    private bool _refreshing;

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(Swatch));
        if (!_refreshing && value != _read())
            _write(value);
    }

    partial void OnChoiceChanged(FieldChoice? value)
    {
        if (!_refreshing && value is not null && value.Value != Value)
            Value = value.Value;
    }

    /// <summary>Reads the value again (after Undo, or another edit).</summary>
    public void Refresh()
    {
        var now = _read();
        if (now == Value)
            return;
        _refreshing = true;
        Value = now;
        Choice = Field.Choices.FirstOrDefault(c => c.Value == now) ?? Choice;
        OnPropertyChanged(nameof(IsOn));
        _refreshing = false;
    }
}

/// <summary>A problem the kind's rules found; clicking it selects the shapes concerned.</summary>
public sealed partial class ProblemViewModel(DiagramProblem problem, Action<DiagramProblem> show) : ObservableObject
{
    public DiagramProblem Problem { get; } = problem;
    public string Message => Problem.Message;
    public string Icon => Problem.Severity switch
    {
        ProblemSeverity.Error => IconKind.Error,
        ProblemSeverity.Warning => IconKind.Warning,
        _ => IconKind.Info,
    };

    [RelayCommand]
    private void Show() => show(Problem);

    public override string ToString() => Message;
}

/// <summary>A kind's command or a text export in the toolbar's menus.</summary>
public sealed partial class EditorCommandViewModel(string name, string icon, string description, Func<Task> run) : ObservableObject
{
    public string Name { get; } = name;
    public string Icon { get; } = icon;
    public string Description { get; } = description;

    [RelayCommand]
    private Task Run() => run();

    public override string ToString() => Name;
}

/// <summary>Pen colours offered for freehand drawing.</summary>
public static class PenColors
{
    public static IReadOnlyList<string> All { get; } = ["#1F1F1F", "#E5484D", "#F59E0B", "#30A46C", "#3574F0", "#8E4EC6", "#FFFFFF"];
}

/// <summary>Zoom presets for the toolbar.</summary>
public static class ZoomLevels
{
    public static string Text(double zoom) => L.T($"{Math.Round(zoom * 100)} %");
}
