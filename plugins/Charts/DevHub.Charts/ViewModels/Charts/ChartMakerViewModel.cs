using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Charts.Core;
using DevHub.Controls;
using DevHub.Integration;
using DevHub.Sdk.Core.Charts;
using DevHub.Services;
using DevHub.ViewModels.Documents;

namespace DevHub.ViewModels.Charts;

/// <summary>A chart type in the picker.</summary>
public sealed record KindChoice(ChartKind Kind)
{
    public override string ToString() => ChartKinds.Name(Kind);
}

/// <summary>A series' style row: dash, markers, values, bars or line (combo charts), color.</summary>
public sealed partial class SeriesStyleRow : ObservableObject
{
    private readonly Action _changed;

    public SeriesStyleRow(SeriesSpec spec, Action changed)
    {
        Name = spec.Name;
        _dash = spec.Dash;
        _markers = spec.Markers;
        _valueLabels = spec.ValueLabels;
        _kind = spec.Kind;
        _color = spec.Color is { } c ? Colors[c] : Colors[0];
        _changed = changed;
    }

    public string Name { get; }

    public static IReadOnlyList<LineDash> Dashes { get; } = Enum.GetValues<LineDash>();
    public static IReadOnlyList<SeriesKind> Kinds { get; } = Enum.GetValues<SeriesKind>();
    public static IReadOnlyList<string> Colors { get; } = ["Automatic", "Color 1", "Color 2", "Color 3", "Color 4", "Color 5", "Color 6"];

    [ObservableProperty] private LineDash _dash;
    [ObservableProperty] private bool _markers;
    [ObservableProperty] private bool _valueLabels;
    [ObservableProperty] private SeriesKind _kind;
    [ObservableProperty] private string _color;

    partial void OnDashChanged(LineDash value) => _changed();
    partial void OnMarkersChanged(bool value) => _changed();
    partial void OnValueLabelsChanged(bool value) => _changed();
    partial void OnKindChanged(SeriesKind value) => _changed();
    partial void OnColorChanged(string value) => _changed();

    public SeriesSpec ToSpec()
    {
        var index = Colors.ToList().IndexOf(Color);
        return new SeriesSpec { Name = Name, Dash = Dash, Markers = Markers, ValueLabels = ValueLabels, Kind = Kind, Color = index > 0 ? index : null };
    }

    public override string ToString() => Name;
}

/// <summary>
/// The chart maker: paste or type a table (or open a CSV), pick a chart type, titles and each series' style, and see it
/// at once; save it (<c>*.devhub-chart.json</c>) to come back to, and export it as an image (or its table) for a report.
/// Other plugins' charts open here too (Reports' "Open in Chart Maker").
/// </summary>
public sealed partial class ChartMakerViewModel : DocumentViewModelBase
{
    private readonly IDialogService _dialogs;
    private readonly IExportService _export;
    private readonly IStatusService _status;
    private bool _loading;
    private bool _dirty;

    public ChartMakerViewModel(IDialogService dialogs, IExportService export, IStatusService status)
    {
        _dialogs = dialogs;
        _export = export;
        _status = status;
        foreach (var kind in Enum.GetValues<ChartKind>())
            KindChoices.Add(new KindChoice(kind));
        Load(ChartSpec.Sample, null);
    }

    public override string Title => _path is null ? "Chart Maker" : $"Chart: {Path.GetFileName(_path)}";
    public override string Icon => IconKind.Palette;
    public override bool FillsEditorArea => true;
    public override DocumentNavigation Navigation => new() { Text = "Chart Maker", Menu = "File", MenuOrder = 60, WelcomeOrder = 70 };
    public override bool HasUnsavedChanges => _dirty && _path is not null;

    private string? _path;

    public ObservableCollection<KindChoice> KindChoices { get; } = [];
    [ObservableProperty] private KindChoice? _kind;
    [ObservableProperty] private string _table = "";
    [ObservableProperty] private string _chartTitle = "";
    [ObservableProperty] private string _xTitle = "";
    [ObservableProperty] private string _yTitle = "";
    [ObservableProperty] private string _valueSuffix = "";
    [ObservableProperty] private bool _seriesInRows;
    [ObservableProperty] private bool _trafficColors;
    public ObservableCollection<SeriesStyleRow> Series { get; } = [];

    [ObservableProperty] private ChartData _data = ChartData.Empty;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private bool _hasChart;
    public bool IsHeatmap => Kind?.Kind == ChartKind.Heatmap;

    partial void OnKindChanged(KindChoice? value) { OnPropertyChanged(nameof(IsHeatmap)); Changed(); }
    partial void OnTableChanged(string value) => Changed();
    partial void OnChartTitleChanged(string value) => Changed();
    partial void OnXTitleChanged(string value) => Changed();
    partial void OnYTitleChanged(string value) => Changed();
    partial void OnValueSuffixChanged(string value) => Changed();
    partial void OnSeriesInRowsChanged(bool value) => Changed();
    partial void OnTrafficColorsChanged(bool value) => Changed();

    /// <summary>Shows a chart from elsewhere (IChartMaker): its kind, data and styles; not tied to a file.</summary>
    public void Show(ChartKind kind, ChartData data) => Load(ChartSpec.From(kind, data), null);

    private ChartSpec Spec => new()
    {
        Title = ChartTitle,
        XTitle = XTitle,
        YTitle = YTitle,
        ValueSuffix = ValueSuffix,
        Kind = Kind?.Kind ?? ChartKind.Bar,
        Table = Table,
        SeriesInRows = SeriesInRows,
        TrafficColors = TrafficColors,
        Series = Series.Select(s => s.ToSpec()).ToList(),
    };

    private void Load(ChartSpec spec, string? path)
    {
        _loading = true;
        try
        {
            _path = path;
            Kind = KindChoices.First(k => k.Kind == spec.Kind);
            Table = spec.Table;
            ChartTitle = spec.Title;
            XTitle = spec.XTitle;
            YTitle = spec.YTitle;
            ValueSuffix = spec.ValueSuffix;
            SeriesInRows = spec.SeriesInRows;
            TrafficColors = spec.TrafficColors;
            Series.Clear();
            foreach (var s in spec.Series)
                Series.Add(new SeriesStyleRow(s, Changed));
        }
        finally
        {
            _loading = false;
        }
        _dirty = false;
        Rebuild();
        OnPropertyChanged(nameof(Title));
    }

    private void Changed()
    {
        if (_loading)
            return;
        _dirty = true;
        Rebuild();
    }

    // Draws the table; keeps a style row for every series it finds (new ones start automatic).
    private void Rebuild()
    {
        var spec = Spec;
        var (data, problem) = ChartBuilder.Build(spec);
        Problem = problem;
        if (problem is null)
        {
            var names = data.Series.Select(s => s.Name).ToList();
            if (!names.SequenceEqual(Series.Select(s => s.Name)))
            {
                var kept = Series.ToDictionary(s => s.Name);
                Series.Clear();
                foreach (var name in names)
                    Series.Add(kept.GetValueOrDefault(name) ?? new SeriesStyleRow(new SeriesSpec { Name = name }, Changed));
                (data, _) = ChartBuilder.Build(Spec);
            }
        }
        Data = data;
        HasChart = problem is null;
    }

    [RelayCommand]
    private void New() => Load(new ChartSpec { Table = "Label\tValue\nA\t3\nB\t5\nC\t2\n" }, null);

    [RelayCommand]
    private void Sample() => Load(ChartSpec.Sample, null);

    [RelayCommand]
    private async Task OpenAsync()
    {
        var path = await _dialogs.PickFileAsync("Open Chart or Table", "Charts and tables", ["*." + ChartSpec.Extension, "*.csv", "*.tsv", "*.txt"]);
        if (path is null)
            return;
        try
        {
            var text = await File.ReadAllTextAsync(path);
            if (path.EndsWith("." + ChartSpec.Extension, StringComparison.OrdinalIgnoreCase))
                Load(ChartSpec.FromJson(text), path);
            else
                Load(new ChartSpec { Table = text, Title = Path.GetFileNameWithoutExtension(path) }, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _status.Show($"Couldn't open {Path.GetFileName(path)}: {e.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var path = _path ?? await _dialogs.PickSaveFileAsync("Save Chart", (ChartTitle.Trim().Length > 0 ? ChartTitle.Trim() : "chart") + "." + ChartSpec.Extension,
            "DevHub chart", ChartSpec.Extension);
        if (path is null)
            return;
        try
        {
            await File.WriteAllTextAsync(path, Spec.ToJson());
            _path = path;
            _dirty = false;
            OnPropertyChanged(nameof(Title));
            _status.Show($"Saved {Path.GetFileName(path)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status.Show($"Couldn't save: {e.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        var keep = _path;
        _path = null;
        await SaveAsync();
        _path ??= keep;
    }

    private string ExportName => ChartTitle.Trim().Length > 0 ? ChartTitle.Trim() : "chart";

    [RelayCommand]
    private Task SaveImageAsync() => HasChart ? _export.SaveChartAsync(Kind?.Kind ?? ChartKind.Bar, Data, ExportName) : Task.CompletedTask;

    [RelayCommand]
    private Task CopyImageAsync() => HasChart ? _export.CopyChartAsync(Kind?.Kind ?? ChartKind.Bar, Data) : Task.CompletedTask;

    [RelayCommand]
    private Task CopyTableAsync() => HasChart ? _export.CopyTableAsync(Data.ToTable(XTitle)) : Task.CompletedTask;
}

/// <summary>Other plugins open their charts here (Reports' "Open in Chart Maker").</summary>
public sealed class ChartMaker(ChartMakerViewModel maker, IServiceProvider services) : IChartMaker
{
    public void Open(ChartKind kind, ChartData data)
    {
        maker.Show(kind, data);
        ((IDocumentService)services.GetService(typeof(IDocumentService))!).OpenById(maker.Id);
    }
}
