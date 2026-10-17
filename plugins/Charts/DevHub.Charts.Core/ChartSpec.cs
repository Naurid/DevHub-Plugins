using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Sdk.Core.Charts;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Charts.Core;

/// <summary>How one series looks in the chart maker (its name is the column's header).</summary>
public sealed record SeriesSpec
{
    public string Name { get; init; } = "";
    public LineDash Dash { get; init; } = LineDash.Solid;
    public bool Markers { get; init; }
    public bool ValueLabels { get; init; }
    public SeriesKind Kind { get; init; } = SeriesKind.Line;

    /// <summary>Palette color 1–6; null = by position.</summary>
    public int? Color { get; init; }
}

/// <summary>
/// A chart as the chart maker keeps it (<c>*.devhub-chart.json</c>): the table as text (tab- or comma-separated, the first
/// row the series' names, the first column the labels), the kind, titles and each series' style.
/// </summary>
public sealed record ChartSpec
{
    public const string Extension = "devhub-chart.json";

    public int Schema { get; init; } = 1;
    public string Title { get; init; } = "";
    public string XTitle { get; init; } = "";
    public string YTitle { get; init; } = "";
    public string ValueSuffix { get; init; } = "";
    public ChartKind Kind { get; init; } = ChartKind.Bar;

    /// <summary>The table, as pasted or typed.</summary>
    public string Table { get; init; } = "";

    /// <summary>Each row is a series (instead of each column).</summary>
    public bool SeriesInRows { get; init; }

    /// <summary>A heatmap in green, amber, red.</summary>
    public bool TrafficColors { get; init; }

    public IReadOnlyList<SeriesSpec> Series { get; init; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ChartSpec FromJson(string json) => JsonSerializer.Deserialize<ChartSpec>(json, Json) ?? new ChartSpec();

    /// <summary>A sample to start from: velocity of four sprints.</summary>
    public static ChartSpec Sample { get; } = new()
    {
        Title = L.T("Velocity"),
        XTitle = L.T("Sprint"),
        YTitle = L.T("Story points"),
        Kind = ChartKind.Combo,
        Table = L.T("Sprint\tCommitted\tCompleted\nSprint 1\t21\t13\nSprint 2\t20\t18\nSprint 3\t22\t21\nSprint 4\t24\t23\n"),
        Series = [new SeriesSpec { Name = L.T("Committed"), Kind = SeriesKind.Bar }, new SeriesSpec { Name = L.T("Completed"), Markers = true, ValueLabels = true }],
    };

    /// <summary>A chart from elsewhere (a report) as a spec: its numbers as a tab-separated table, its titles and styles.</summary>
    public static ChartSpec From(ChartKind kind, ChartData data)
    {
        var text = new StringBuilder();
        text.Append(Clean(data.XTitle ?? "")).Append('\t').AppendJoin('\t', data.Series.Select(s => Clean(s.Name))).Append('\n');
        for (var i = 0; i < data.Labels.Count; i++)
        {
            text.Append(Clean(data.Labels[i]));
            foreach (var s in data.Series)
                text.Append('\t').Append(i < s.Values.Count && s.Values[i] is { } v ? v.ToString(CultureInfo.InvariantCulture) : "");
            text.Append('\n');
        }
        return new ChartSpec
        {
            Title = data.Title ?? "",
            XTitle = data.XTitle ?? "",
            YTitle = data.YTitle ?? "",
            ValueSuffix = data.ValueSuffix,
            Kind = kind,
            Table = text.ToString(),
            TrafficColors = data.TrafficColors,
            Series = data.Series.Select(s => new SeriesSpec
            {
                Name = s.Name, Dash = s.Style.Dash, Markers = s.Style.Markers, ValueLabels = s.Style.ValueLabels, Kind = s.Style.Kind,
                Color = s.Style.Color is { } c ? c + 1 : null,
            }).ToList(),
        };
    }

    private static string Clean(string cell) => cell.Replace('\t', ' ').Replace('\n', ' ').Replace("\r", "");
}

/// <summary>Reading a table: from Excel / Sheets / Word (tab-separated) or CSV (comma or semicolon, quotes).</summary>
public static class TableText
{
    /// <summary>The rows and cells of <paramref name="text"/> (empty rows skipped, cells trimmed).</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var separator = lines.Contains('\t') ? '\t' : lines.Count(c => c == ';') > lines.Count(c => c == ',') ? ';' : ',';
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var c = lines[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < lines.Length && lines[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                    quoted = false;
                else
                    cell.Append(c);
            }
            else if (c == '"' && cell.ToString().Trim().Length == 0)
                quoted = true;
            else if (c == separator)
            {
                row.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else if (c == '\n')
            {
                row.Add(cell.ToString().Trim());
                cell.Clear();
                if (row.Any(x => x.Length > 0))
                    rows.Add(row);
                row = [];
            }
            else
                cell.Append(c);
        }
        row.Add(cell.ToString().Trim());
        if (row.Any(x => x.Length > 0))
            rows.Add(row);
        return rows;
    }

    /// <summary>A number as people type it: "12.5", "12,5", "1 250", "45%", "€ 12" (null when it isn't one).</summary>
    public static double? Number(string cell)
    {
        var text = new string(cell.Where(c => char.IsDigit(c) || c is '.' or ',' or '-' or '+' or 'e' or 'E').ToArray());
        if (text.Length == 0 || !text.Any(char.IsDigit))
            return null;
        // One comma and no dot is a decimal comma ("12,5"); otherwise commas group thousands ("1,250.5").
        if (text.Count(c => c == ',') == 1 && !text.Contains('.'))
            text = text.Replace(',', '.');
        else
            text = text.Replace(",", "");
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

/// <summary>Turns a spec into the chart's data, and tells what's wrong with the table when it can't.</summary>
public static class ChartBuilder
{
    /// <summary>The data to draw, or the reason there's none ("Paste a table…").</summary>
    public static (ChartData Data, string? Problem) Build(ChartSpec spec)
    {
        var rows = TableText.Parse(spec.Table);
        if (rows.Count == 0)
            return (ChartData.Empty, L.T("Paste a table: the first row names the series, the first column the labels (copy cells from Excel, Sheets or Word)."));
        var width = rows.Max(r => r.Count);
        string Cell(int r, int c) => c < rows[r].Count ? rows[r][c] : "";
        if (width < 2 || rows.Count < 2)
            return (ChartData.Empty, L.T("A table needs a header row and at least one row of numbers, with labels in the first column."));

        List<string> labels;
        List<(string Name, List<double?> Values)> series;
        if (spec.SeriesInRows)
        {
            labels = Enumerable.Range(1, width - 1).Select(c => Cell(0, c)).ToList();
            series = Enumerable.Range(1, rows.Count - 1).Select(r => (Cell(r, 0), Enumerable.Range(1, width - 1).Select(c => TableText.Number(Cell(r, c))).ToList())).ToList();
        }
        else
        {
            labels = Enumerable.Range(1, rows.Count - 1).Select(r => Cell(r, 0)).ToList();
            series = Enumerable.Range(1, width - 1).Select(c => (Cell(0, c), Enumerable.Range(1, rows.Count - 1).Select(r => TableText.Number(Cell(r, c))).ToList())).ToList();
        }
        series = series.Where(s => s.Values.Any(v => v is not null)).ToList();
        if (series.Count == 0)
            return (ChartData.Empty, L.T("No numbers found: the cells after the first column (or row) should hold numbers."));

        var data = new ChartData(labels, series.Select((s, i) =>
        {
            var name = s.Name.Length > 0 ? s.Name : L.T($"Series {i + 1}");
            var style = spec.Series.FirstOrDefault(x => x.Name == name);
            return new ChartSeries(name, s.Values)
            {
                Style = style is null ? SeriesStyle.Default : new SeriesStyle
                {
                    Dash = style.Dash, Markers = style.Markers, ValueLabels = style.ValueLabels, Kind = style.Kind,
                    Color = style.Color is >= 1 and <= 6 ? style.Color - 1 : null,
                },
            };
        }).ToList())
        {
            Title = spec.Title.Trim().Length > 0 ? spec.Title.Trim() : null,
            XTitle = spec.XTitle.Trim().Length > 0 ? spec.XTitle.Trim() : null,
            YTitle = spec.YTitle.Trim().Length > 0 ? spec.YTitle.Trim() : null,
            ValueSuffix = spec.ValueSuffix,
            TrafficColors = spec.TrafficColors,
        };
        return (data, null);
    }
}
