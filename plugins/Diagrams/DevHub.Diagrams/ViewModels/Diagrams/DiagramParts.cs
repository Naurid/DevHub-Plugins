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

    /// <summary>The colour picker's swatches (built when the picker first opens).</summary>
    public IReadOnlyList<ColorSwatch> Palette => _palette ??= ColorSwatch.Palette.Select(hex => new ColorSwatch(hex, v => Value = v)).ToList();
    private IReadOnlyList<ColorSwatch>? _palette;

    /// <summary>Back to the diagram's colour.</summary>
    [RelayCommand]
    private void ClearColor() => Value = "";

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

/// <summary>A colour in the picker: clicking it sets the field.</summary>
public sealed class ColorSwatch(string hex, Action<string> pick)
{
    public string Hex { get; } = hex;
    public RelayCommand PickCommand { get; } = new(() => pick(hex));
    public override string ToString() => Hex;

    /// <summary>12 hues from red round to pink, 5 shades each (light to dark), then a row of greys.</summary>
    public static IReadOnlyList<string> Palette { get; } = Build();

    private static List<string> Build()
    {
        var colours = new List<string>();
        double[] lightness = [0.88, 0.72, 0.55, 0.42, 0.28];
        foreach (var l in lightness)
            for (var hue = 0; hue < 360; hue += 30)
                colours.Add(Hsl(hue, l > 0.8 ? 0.7 : 0.75, l));
        for (var i = 0; i < 12; i++)
        {
            var v = (int)Math.Round(255 * (1 - i / 11.0));
            colours.Add($"#{v:X2}{v:X2}{v:X2}");
        }
        return colours;
    }

    private static string Hsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static int B(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return $"#{B(r + m):X2}{B(g + m):X2}{B(b + m):X2}";
    }
}
