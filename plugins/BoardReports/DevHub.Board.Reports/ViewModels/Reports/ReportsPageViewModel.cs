using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board;
using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Search;
using DevHub.Board.Reports.Core;
using BaselineStore = DevHub.Board.Reports.Core.Baselines;
using DevHub.Sdk.Core.Charts;
using DevHub.Sdk.Core.Settings;
using DevHub.Sdk.Core.Tables;
using DevHub.Integration;
using DevHub.Services;
using DevHub.ViewModels.Board;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Reports;

/// <summary>The Reports page of every board.</summary>
public sealed class ReportsPageProvider(ISettingsStore settings, IExportService export, IChartMaker? chartMaker = null) : IBoardPageProvider
{
    public string Id => "reports";
    public string Title => "Reports";
    public int Order => 700;
    public BoardPageViewModel Create(BoardPageHost host) => new ReportsPageViewModel(host, settings, export, chartMaker);
}

/// <summary>A report in the list on the left, with the chart type it starts with.</summary>
public sealed record ReportKind(string Id, string Name, string Description, bool NeedsSprint, ChartKind DefaultChart = ChartKind.Line, string? Group = null)
{
    public override string ToString() => Name;
}

/// <summary>A chart type in the picker.</summary>
public sealed record ChartKindItem(ChartKind Kind)
{
    public override string ToString() => ChartKinds.Name(Kind);
}

/// <summary>A gadget as shown: title, a chart or a list of issues.</summary>
public sealed partial class GadgetViewModel(int index, string title, ReportsPageViewModel page) : ObservableObject
{
    public int Index { get; } = index;
    public string Title { get; } = title;
    [ObservableProperty] private ChartData _chart = ChartData.Empty;
    [ObservableProperty] private bool _isChart;
    public ObservableCollection<IssueItemViewModel> Issues { get; } = [];
    [ObservableProperty] private string? _note;
    public IRelayCommand RemoveCommand => new RelayCommand(() => page.RemoveGadget(Index));
    public override string ToString() => Title;
}

/// <summary>A row of a report's list (sprint report, epic progress, scope changes, sprint plans).</summary>
public sealed record ReportRow(string Key, string Summary, string Detail, double Fraction)
{
    public bool HasFraction => Fraction >= 0;
    public override string ToString() => $"{Key} {Summary} {Detail}";
}

/// <summary>A forecast scenario's line, or a baseline comparison's.</summary>
public sealed record ForecastRow(string Name, string Velocity, string Result)
{
    public override string ToString() => $"{Name}: {Velocity}, {Result}";
}

/// <summary>
/// Reports for the board: pick one on the left (sprint reports pick a sprint), the chart and its numbers on the right,
/// with what it means for steering. Each chart can be shown as another fitting chart type (remembered per report) and
/// exported (PNG, copy, CSV). The Forecast has its own inputs (scope, three velocities, a planned ramp-up) and baselines;
/// the Dashboard is a column of gadgets you add and remove (kept per board in Board.Reports' settings).
/// </summary>
public sealed partial class ReportsPageViewModel : BoardPageViewModel, ICardHost
{
    private readonly ISettingsStore _settings;
    private readonly IExportService _export;
    private readonly IChartMaker? _chartMaker;
    private TableData _table = TableData.Empty;
    private Forecast? _forecast;
    private bool _refreshing;

    public ReportsPageViewModel(BoardPageHost host, ISettingsStore settings, IExportService export, IChartMaker? chartMaker = null) : base(host)
    {
        _settings = settings;
        _export = export;
        _chartMaker = chartMaker;
        Kinds =
        [
            new("dashboard", "Dashboard", "Gadgets you choose: your issues, issues by a field, saved searches.", false),
            new("forecast", "Release forecast", "When the remaining scope will be done: pessimistic, neutral and optimistic velocity.", false, ChartKind.Line),
            new("scope", "Scope change", "Issues added, re-estimated or dropped since a baseline or over a period.", false, ChartKind.Bar),
            new("plans", "Sprint plans", "Every sprint: dates, goal, capacity, team days, planned and completed points, stories.", false, ChartKind.Combo),
            new("burndown", "Burndown", "Points left in a sprint, day by day, against the ideal line.", true),
            new("burnup", "Burnup", "Points done and the sprint's scope, day by day: scope changes show.", true),
            new("sprint", "Sprint report", "What a sprint finished, what it didn't, what was added and removed.", true),
            new("velocity", "Velocity", "Points committed and completed in the last sprints.", false, ChartKind.Bar),
            new("reliability", "Commitment reliability", "Completed points as a share of committed points, per sprint.", false, ChartKind.Bar),
            new("throughput", "Throughput", "Issues and points finished per sprint.", false, ChartKind.Combo),
            new("capacity", "Capacity and load", "Capacity, points taken on and points completed, per sprint.", false, ChartKind.Combo),
            new("leadtime", "Lead time", "Days from created to done for issues resolved in the last 90 days.", false),
            new("control", "Control chart", "Cycle time (first In Progress to resolved) of issues resolved in the last 90 days.", false),
            new("flow", "Cumulative flow", "Issues per status category each day of the last 30 days.", false, ChartKind.StackedArea),
            new("wip", "Work in progress", "Issues in progress each day of the last 30 days.", false, ChartKind.Area),
            new("defects", "Defects", "Bugs found and fixed in each sprint.", false, ChartKind.Bar),
            new("epics", "Epic progress", "Every epic's done and remaining issues and points.", false, ChartKind.HorizontalBar),
        ];
        _kind = Kinds[1];
        LoadForecastSettings();
        Refresh();
    }

    public override string Title => "Reports";

    System.Windows.Input.ICommand ICardHost.SelectCommand => SelectIssueCommand;
    System.Windows.Input.ICommand ICardHost.OpenCommand => OpenIssueCommand;

    public IReadOnlyList<ReportKind> Kinds { get; }
    [ObservableProperty] private ReportKind _kind;
    public ObservableCollection<Sprint> Sprints { get; } = [];
    [ObservableProperty] private Sprint? _sprint;
    [ObservableProperty] private bool _showSprints;
    [ObservableProperty] private bool _isDashboard;
    [ObservableProperty] private bool _isForecast;
    [ObservableProperty] private bool _isScope;

    [ObservableProperty] private ChartData _chart = ChartData.Empty;
    [ObservableProperty] private bool _hasChart;
    public ObservableCollection<ChartKindItem> ChartKindChoices { get; } = [];
    [ObservableProperty] private ChartKindItem? _selectedChartKind;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _explanation = "";
    [ObservableProperty] private string? _empty;
    public ObservableCollection<ReportRow> Rows { get; } = [];
    public bool HasRows => Rows.Count > 0;
    [ObservableProperty] private string _rowsTitle = "";
    [ObservableProperty] private bool _canExportTable;

    public ObservableCollection<GadgetViewModel> Gadgets { get; } = [];

    public override void OnBoardChanged() => Refresh();

    partial void OnKindChanged(ReportKind value) => Refresh();

    partial void OnSprintChanged(Sprint? value)
    {
        if (!_refreshing)
            Refresh();
    }

    partial void OnSelectedChartKindChanged(ChartKindItem? value)
    {
        if (_refreshing || value is null)
            return;
        _settings.UpdateSection<BoardReportsSettings>(s => s.ChartKinds[Kind.Id] = value.Kind.ToString());
    }

    public void Refresh()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            var board = Context.Snapshot;
            var now = DateTimeOffset.Now;
            var keep = Sprint?.Id;
            Sprints.Clear();
            foreach (var s in board.Sprints.Values.Where(s => s.State != SprintState.Future).OrderByDescending(s => s.Start))
                Sprints.Add(s);
            Sprint = Sprints.FirstOrDefault(s => s.Id == keep) ?? Sprints.FirstOrDefault(s => s.State == SprintState.Active) ?? Sprints.FirstOrDefault();
            ShowSprints = Kind.NeedsSprint;
            IsDashboard = Kind.Id == "dashboard";
            IsForecast = Kind.Id == "forecast";
            IsScope = Kind.Id == "scope";
            Rows.Clear();
            Empty = null;
            Chart = ChartData.Empty;
            _table = TableData.Empty;
            Summary = Kind.Description;
            Explanation = Kpis.Explain(Kind.Id);
            RowsTitle = "";

            switch (Kind.Id)
            {
                case "dashboard":
                    BuildDashboard(board);
                    break;
                case "forecast":
                    BuildForecast(board);
                    break;
                case "scope":
                    BuildScopeChange(board);
                    break;
                case "plans":
                {
                    Chart = Kpis.CapacityLoad(board);
                    _table = Kpis.SprintPlans(board);
                    RowsTitle = "Sprints";
                    foreach (var row in _table.Rows)
                    {
                        var detail = string.Join(" · ", new[]
                        {
                            row[2].Length > 0 ? $"{row[2]} – {row[3]}" : null,
                            row[5].Length > 0 ? $"capacity {row[5]}" : null,
                            row[7].Length > 0 ? $"{row[7]} focused days" : null,
                            row[8].Length > 0 ? $"{(row[1] == nameof(SprintState.Future) ? "planned" : "committed")} {row[8]}" : null,
                            row[9].Length > 0 ? $"completed {row[9]}" : null,
                        }.OfType<string>());
                        Rows.Add(new ReportRow(row[0], row[4], detail, double.TryParse(row[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var planned) && planned > 0
                            && double.TryParse(row[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var done) ? Math.Min(1, done / planned) : -1));
                    }
                    if (Rows.Count == 0)
                        Empty = "No sprint yet (sprints come with the Board.Scrum plugin).";
                    break;
                }
                case "burndown" or "burnup" or "sprint" when Sprint is null:
                    Empty = "No sprint has started yet (sprints come with the Board.Scrum plugin).";
                    break;
                case "burndown" or "burnup":
                {
                    var chart = BoardReports.Sprint(board, Sprint!, now);
                    Chart = (Kind.Id == "burndown" ? chart.Burndown : chart.Burnup) with { Title = $"{Kind.Name}: {Sprint!.Name}", YTitle = "Story points" };
                    Summary = $"{Sprint!.Name}: {N(chart.Completed)} of {N(chart.Scope)} points done · {N(chart.Committed)} committed at the start";
                    break;
                }
                case "sprint":
                {
                    var report = BoardReports.SprintReportOf(board, Sprint!, now);
                    Summary = $"{Sprint!.Name}: {N(report.CompletedPoints)} of {N(report.CommittedPoints)} committed points completed";
                    RowsTitle = "Issues";
                    foreach (var i in report.Completed)
                        Rows.Add(new ReportRow(i.Key, i.Summary, "completed", 1));
                    foreach (var i in report.NotCompleted)
                        Rows.Add(new ReportRow(i.Key, i.Summary, "not completed", 0));
                    foreach (var i in report.Added)
                        Rows.Add(new ReportRow(i.Key, i.Summary, "added after the start", 0));
                    foreach (var i in report.Removed)
                        Rows.Add(new ReportRow(i.Key, i.Summary, "removed", 0));
                    if (Rows.Count == 0)
                        Empty = "The sprint has no issues.";
                    break;
                }
                case "velocity":
                    Chart = BoardReports.Velocity(board) with { Title = "Velocity", XTitle = "Sprint", YTitle = "Story points" };
                    if (BoardReports.AverageVelocity(board) is { } v)
                        Chart = Chart with { References = [new ChartReference("Average", Math.Round(v, 1))] };
                    Summary = BoardReports.AverageVelocity(board) is { } average
                        ? $"Average of the last sprints: {N(average)} points"
                        : "No sprint has been completed yet.";
                    if (Chart.Labels.Count == 0)
                        Empty = "Velocity appears after the first completed sprint.";
                    break;
                case "reliability":
                {
                    Chart = Kpis.CommitmentReliability(board);
                    var sprints = Kpis.Closed(board).Where(s => s.CommittedPoints > 0).ToList();
                    Summary = sprints.Count == 0 ? "No sprint has been completed yet."
                        : $"{100 * sprints.Sum(s => s.CompletedPoints) / sprints.Sum(s => s.CommittedPoints):0}% of committed points completed over {sprints.Count} sprints";
                    if (Chart.Labels.Count == 0)
                        Empty = "Appears after the first completed sprint.";
                    break;
                }
                case "throughput":
                    Chart = Kpis.Throughput(board);
                    if (Chart.Labels.Count == 0)
                        Empty = "Appears after the first completed sprint.";
                    break;
                case "capacity":
                    Chart = Kpis.CapacityLoad(board);
                    if (Chart.Labels.Count == 0)
                        Empty = "No sprint yet (sprints come with the Board.Scrum plugin).";
                    break;
                case "leadtime":
                {
                    var leads = Kpis.LeadTimes(board, now);
                    Chart = Kpis.LeadTimeChart(leads);
                    Summary = leads.Count == 0 ? "No issue was resolved in the last 90 days."
                        : $"{leads.Count} issues · average {leads.Average(l => l.Days):0.#} days · median {Median(leads.Select(l => l.Days)):0.#} days";
                    if (leads.Count == 0)
                        Empty = Summary;
                    break;
                }
                case "flow":
                    Chart = BoardReports.CumulativeFlow(board, now) with { Title = "Cumulative flow", YTitle = "Issues" };
                    break;
                case "wip":
                    Chart = Kpis.WorkInProgress(board, now);
                    break;
                case "defects":
                    Chart = Kpis.Defects(board);
                    if (Chart.Labels.Count == 0)
                        Empty = "Appears after the first completed sprint.";
                    break;
                case "control":
                {
                    var cycles = BoardReports.CycleTimes(board, now);
                    Chart = BoardReports.ControlChart(cycles) with { Title = "Control chart", YTitle = "Days" };
                    Summary = cycles.Count == 0 ? "No issue was resolved in the last 90 days."
                        : $"{cycles.Count} issues · average {cycles.Average(c => c.Days):0.#} days · median {Median(cycles.Select(c => c.Days)):0.#} days";
                    if (cycles.Count == 0)
                        Empty = Summary;
                    break;
                }
                case "epics":
                {
                    RowsTitle = "Epics";
                    var epics = BoardReports.Epics(board);
                    foreach (var e in epics)
                        Rows.Add(new ReportRow(e.Epic.Key, e.Epic.Summary,
                            $"{e.DoneIssues}/{e.Issues} issues · {N(e.DonePoints)}/{N(e.Points)} points", e.Fraction));
                    Chart = new ChartData(epics.Select(e => e.Epic.Summary).ToList(),
                    [
                        new ChartSeries("Done", epics.Select(e => (double?)e.DonePoints).ToList()),
                        new ChartSeries("Remaining", epics.Select(e => (double?)(e.Points - e.DonePoints)).ToList()),
                    ]) { Title = "Epic progress", YTitle = "Story points" };
                    if (Rows.Count == 0)
                        Empty = "The board has no epics.";
                    break;
                }
            }

            if (_table.Rows.Count == 0)
                _table = Rows.Count > 0
                    ? new TableData(["Key", "Summary", "Detail"], Rows.Select(r => (IReadOnlyList<string>)[r.Key, r.Summary, r.Detail]).ToList()) { Title = Kind.Name }
                    : Chart.ToTable(Chart.XTitle ?? "");
            HasChart = Chart.Labels.Count > 0 && Empty is null && !IsDashboard;
            CanExportTable = _table.Rows.Count > 0 && !IsDashboard;
            FillChartKinds();
        }
        finally
        {
            OnPropertyChanged(nameof(HasRows));
            _refreshing = false;
        }
    }

    // The chart types that fit this report's data, the remembered one (or the report's own) selected.
    private void FillChartKinds()
    {
        ChartKindChoices.Clear();
        foreach (var kind in ChartKinds.Fitting(Chart))
            ChartKindChoices.Add(new ChartKindItem(kind));
        var saved = _settings.LoadSection<BoardReportsSettings>().ChartKinds.GetValueOrDefault(Kind.Id);
        var wanted = Enum.TryParse<ChartKind>(saved, out var k) ? k : Kind.DefaultChart;
        SelectedChartKind = ChartKindChoices.FirstOrDefault(c => c.Kind == wanted) ?? ChartKindChoices.FirstOrDefault(c => c.Kind == Kind.DefaultChart) ?? ChartKindChoices.FirstOrDefault();
    }

    private static string N(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    // ---- Export -------------------------------------------------------------------------------------------

    private string ExportName => $"{Kind.Name} {Context.Project.Key}{(Kind.NeedsSprint && Sprint is not null ? " " + Sprint.Name : "")}";

    [RelayCommand]
    private Task SaveChartAsync() => _export.SaveChartAsync(SelectedChartKind?.Kind ?? Kind.DefaultChart, Chart, ExportName);

    [RelayCommand]
    private Task CopyChartAsync() => _export.CopyChartAsync(SelectedChartKind?.Kind ?? Kind.DefaultChart, Chart);

    [RelayCommand]
    private Task ExportTableAsync() => _export.SaveTableAsync(_table, ExportName);

    [RelayCommand]
    private Task CopyTableAsync() => _export.CopyTableAsync(_table);

    /// <summary>The Charts plugin is on: the chart can be restyled and edited in its chart maker.</summary>
    public bool CanOpenInChartMaker => _chartMaker is not null;

    [RelayCommand]
    private void OpenInChartMaker() => _chartMaker?.Open(SelectedChartKind?.Kind ?? Kind.DefaultChart, Chart);

    // ---- Forecast and scope change ------------------------------------------------------------------------

    public ObservableCollection<NamedItem> Scopes { get; } = [];
    [ObservableProperty] private NamedItem? _scope;
    [ObservableProperty] private string _pessimistic = "";
    [ObservableProperty] private string _neutral = "";
    [ObservableProperty] private string _optimistic = "";
    [ObservableProperty] private string _planned = "";
    [ObservableProperty] private string _velocityHint = "";
    [ObservableProperty] private string _targetText = "";
    [ObservableProperty] private string? _forecastError;
    public ObservableCollection<ForecastRow> ScenarioRows { get; } = [];

    public ObservableCollection<ForecastBaseline> Baselines { get; } = [];
    [ObservableProperty] private ForecastBaseline? _baseline;
    public bool HasBaselines => Baselines.Count > 0;
    public ObservableCollection<ForecastRow> CompareRows { get; } = [];
    [ObservableProperty] private string _compareText = "";
    [ObservableProperty] private DateTime? _scopeFrom;

    partial void OnScopeChanged(NamedItem? value) => InputsChanged();
    partial void OnPessimisticChanged(string value) => InputsChanged();
    partial void OnNeutralChanged(string value) => InputsChanged();
    partial void OnOptimisticChanged(string value) => InputsChanged();
    partial void OnPlannedChanged(string value) => InputsChanged();

    partial void OnBaselineChanged(ForecastBaseline? value)
    {
        if (_refreshing)
            return;
        if (value is not null)
            ScopeFrom = value.Taken.LocalDateTime.Date;
        Refresh();
    }

    partial void OnScopeFromChanged(DateTime? value)
    {
        if (!_refreshing && IsScope)
            Refresh();
    }

    private bool _loadingInputs;

    private void InputsChanged()
    {
        if (_loadingInputs || _refreshing)
            return;
        _settings.UpdateSection<BoardReportsSettings>(s => s.Forecasts[Context.Location.Key] = new ForecastSettings
        {
            Scope = Scope?.Id ?? "backlog",
            Pessimistic = Pessimistic,
            Neutral = Neutral,
            Optimistic = Optimistic,
            Planned = Planned,
        });
        Refresh();
    }

    private void LoadForecastSettings()
    {
        _loadingInputs = true;
        var saved = _settings.LoadSection<BoardReportsSettings>().Forecasts.GetValueOrDefault(Context.Location.Key) ?? new ForecastSettings();
        FillScopes();
        Scope = Scopes.FirstOrDefault(s => s.Id == saved.Scope) ?? Scopes[0];
        Pessimistic = saved.Pessimistic;
        Neutral = saved.Neutral;
        Optimistic = saved.Optimistic;
        Planned = saved.Planned;
        ScopeFrom = DateTime.Today.AddDays(-30);
        _loadingInputs = false;
    }

    // The scopes a forecast can be made for: everything open, a version, an epic, a saved filter.
    private void FillScopes()
    {
        var board = Context.Snapshot;
        var keep = Scope?.Id;
        Scopes.Clear();
        Scopes.Add(new NamedItem("backlog", "The whole backlog"));
        foreach (var v in board.Versions.Values.Where(v => !v.Archived && !v.Released).OrderBy(v => v.Release ?? DateOnly.MaxValue))
            Scopes.Add(new NamedItem("version:" + v.Id, "Version " + v.Name));
        foreach (var e in board.Issues.Values.Where(i => board.LevelOf(i) == IssueTypeLevel.Epic && !board.IsDone(i)).OrderBy(i => i.Rank, StringComparer.Ordinal))
            Scopes.Add(new NamedItem("epic:" + e.Id, $"Epic {e.Key} {e.Summary}"));
        foreach (var f in board.Filters.Values.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            Scopes.Add(new NamedItem("filter:" + f.Id, "Filter " + f.Name));
        if (keep is not null && Scopes.FirstOrDefault(s => s.Id == keep) is { } same)
            Scope = same;
    }

    /// <summary>The issues of the chosen scope (all of them, done included: the forecast counts what's left) and its target date.</summary>
    private (IReadOnlyList<Issue> Issues, DateOnly? Target) ScopeIssues(BoardSnapshot board)
    {
        var id = Scope?.Id ?? "backlog";
        var all = board.Issues.Values;
        if (id.StartsWith("version:", StringComparison.Ordinal) && board.Versions.GetValueOrDefault(id[8..]) is { } version)
            return (all.Where(i => i.FixVersions.Contains(version.Id)).ToList(), version.Release);
        if (id.StartsWith("epic:", StringComparison.Ordinal) && board.Issue(id[5..]) is { } epic)
            return (all.Where(i => board.EpicOf(i)?.Id == epic.Id).ToList(), epic.Due);
        if (id.StartsWith("filter:", StringComparison.Ordinal) && board.Filters.GetValueOrDefault(id[7..]) is { } filter)
        {
            try
            {
                return (QueryCompiler.Compile(filter.Query, Context.QueryContext()).Run(all).ToList(), null);
            }
            catch (QueryException)
            {
                return ([], null);
            }
        }
        return (all.ToList(), null);
    }

    private static double? Number(string text) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;

    private void BuildForecast(BoardSnapshot board)
    {
        FillScopes();
        ScenarioRows.Clear();
        ForecastError = null;
        var history = ScenarioVelocities.FromHistory(board);
        VelocityHint = history is { } h
            ? $"From the last sprints: {N(h.Pessimistic)} lowest, {N(h.Neutral)} average, {N(h.Optimistic)} highest (leave a box empty to use it)."
            : "No sprint has finished yet: type the velocities you expect (a new team usually starts low).";
        var planned = Planned.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(Number).ToList();
        if (planned.Any(p => p is null))
        {
            ForecastError = "Planned velocities are numbers separated by commas (e.g. 5, 8, 12).";
            return;
        }
        double? Pick(string text, double? fallback) => text.Trim().Length == 0 ? fallback : Number(text);
        var neutral = Pick(Neutral, history?.Neutral ?? planned.LastOrDefault());
        var pessimistic = Pick(Pessimistic, history?.Pessimistic ?? neutral * 0.7);
        var optimistic = Pick(Optimistic, history?.Optimistic ?? neutral * 1.3);
        if (neutral is not > 0 || pessimistic is null || optimistic is null)
        {
            ForecastError = "Type a neutral velocity (points per sprint) to forecast.";
            Empty = "No velocity to forecast with yet.";
            return;
        }
        var (issues, target) = ScopeIssues(board);
        var today = DateOnly.FromDateTime(DateTime.Today);
        _forecast = Forecasts.Build(board, issues, new ScenarioVelocities(pessimistic.Value, neutral.Value, optimistic.Value), today, target,
            planned.Count > 0 ? planned.Select(p => p!.Value).ToList() : null);
        Chart = _forecast.Chart;
        foreach (var s in _forecast.Scenarios)
            ScenarioRows.Add(new ForecastRow(s.Name, $"{N(s.Velocity)} points per sprint", s.Text));
        TargetText = target is { } t ? $"Target: {t:d MMM yyyy}" : "No target date (versions have release dates, epics due dates)";
        Summary = $"{N(_forecast.Remaining)} of {N(_forecast.ScopePoints)} points left" +
                  (_forecast.Unestimated > 0 ? $" · {_forecast.Unestimated} open issues not estimated (counted as 0)" : "") +
                  $" · next sprint starts {_forecast.NextSprintStart:d MMM}, {_forecast.SprintDays} days each";
        _table = new TableData(["Scenario", "Velocity", "Sprints", "Delivery", "Days late (+) or early (−)"],
            _forecast.Scenarios.Select(s => (IReadOnlyList<string>)[s.Name, N(s.Velocity), s.Sprints?.ToString(CultureInfo.InvariantCulture) ?? "",
                s.Delivery?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", s.DaysLate?.ToString(CultureInfo.InvariantCulture) ?? ""]).ToList()) { Title = "Release forecast" };
        FillBaselines(board);
    }

    private void FillBaselines(BoardSnapshot board)
    {
        var keep = Baseline?.Id;
        Baselines.Clear();
        foreach (var b in BaselineStore.All(board).Where(b => b.Scope == (Scope?.Id ?? "backlog")).OrderByDescending(b => b.Taken))
            Baselines.Add(b);
        Baseline = Baselines.FirstOrDefault(b => b.Id == keep) ?? Baselines.FirstOrDefault();
        OnPropertyChanged(nameof(HasBaselines));
        CompareRows.Clear();
        CompareText = "";
        if (Baseline is not { } baseline || _forecast is null)
            return;
        CompareText = $"Since \"{baseline.Name}\" ({baseline.Taken.LocalDateTime:d MMM}): scope {N(baseline.ScopePoints)} → {N(_forecast.ScopePoints)} points, " +
                      $"left {N(baseline.ScopePoints - baseline.DonePoints)} → {N(_forecast.Remaining)}";
        foreach (var (name, before, now, moved) in BaselineStore.Compare(baseline, _forecast))
            CompareRows.Add(new ForecastRow(name, $"{before} → {now}", moved switch
            {
                null => "",
                0 => "same date",
                > 0 => $"{moved} days later",
                _ => $"{-moved} days earlier",
            }));
    }

    [RelayCommand]
    private async Task SaveBaselineAsync()
    {
        if (_forecast is null)
            return;
        var dialog = new TextInputDialogViewModel("Save Baseline", "Name", $"Before {DateTime.Today:d MMM}", "Save")
        {
            Explanation = "A baseline keeps today's forecast, to compare the dates after a change of scope (Scope change report).",
        };
        if (await Context.Dialogs.ShowDialogAsync(dialog))
            await Context.RunAsync(new SaveBaseline(dialog.Text, Scope?.Id ?? "backlog", _forecast));
    }

    [RelayCommand]
    private async Task DeleteBaselineAsync()
    {
        if (Baseline is { } baseline && await Context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Delete Baseline",
                $"Delete the baseline \"{baseline.Name}\"?", "Delete", [baseline.Name])))
            await Context.RunAsync(new DeleteBaseline(baseline.Id));
    }

    private void BuildScopeChange(BoardSnapshot board)
    {
        FillScopes();
        FillBaselines(board);
        var from = new DateTimeOffset(ScopeFrom ?? DateTime.Today.AddDays(-30));
        var (issues, _) = ScopeIssues(board);
        var changes = Kpis.ScopeChanges(board, issues, from, DateTimeOffset.Now);
        Chart = Kpis.ScopeChangeChart(board, changes);
        RowsTitle = "Changes";
        foreach (var c in changes)
            Rows.Add(new ReportRow(c.Issue.Key, c.Issue.Summary, $"{c.What} · {(c.Points >= 0 ? "+" : "")}{N(c.Points)} points · {c.At.LocalDateTime:d MMM}", -1));
        var added = changes.Where(c => c.Points > 0).Sum(c => c.Points);
        var removed = -changes.Where(c => c.Points < 0).Sum(c => c.Points);
        Summary = $"{Scope?.Name}, since {from.LocalDateTime:d MMM yyyy}: +{N(added)} points added, −{N(removed)} removed, net {(added - removed >= 0 ? "+" : "")}{N(added - removed)}";
        if (changes.Count == 0)
            Empty = "No issue was added, re-estimated or dropped in this period.";
        _table = new TableData(["Key", "Summary", "Epic", "Change", "Points", "When"],
            changes.Select(c => (IReadOnlyList<string>)[c.Issue.Key, c.Issue.Summary, board.EpicOf(c.Issue)?.Key ?? "", c.What,
                c.Points.ToString("0.##", CultureInfo.InvariantCulture), c.At.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)]).ToList()) { Title = "Scope change" };
    }

    // ---- Dashboard ----------------------------------------------------------------------------------------

    private List<GadgetConfig> Configs()
    {
        var all = _settings.LoadSection<BoardReportsSettings>().Dashboards;
        return all.TryGetValue(Context.Location.Key, out var list) ? list
            : [new GadgetConfig { Kind = "assigned" }, new GadgetConfig { Kind = "pie", Setting = IssueFields.Status }];
    }

    private void SaveConfigs(List<GadgetConfig> configs) =>
        _settings.UpdateSection<BoardReportsSettings>(s => s.Dashboards[Context.Location.Key] = configs);

    private void BuildDashboard(BoardSnapshot board)
    {
        Gadgets.Clear();
        var configs = Configs();
        var context = Context.QueryContext();
        for (var i = 0; i < configs.Count; i++)
        {
            var config = configs[i];
            switch (config.Kind)
            {
                case "assigned":
                {
                    var gadget = new GadgetViewModel(i, "Assigned to me", this);
                    foreach (var issue in board.Issues.Values.Where(x => x.Assignee == Context.Me && !board.IsDone(x))
                                 .OrderBy(x => board.Project.Priorities.ToList().FindIndex(p => p.Id == x.Priority)).Take(20))
                        gadget.Issues.Add(new IssueItemViewModel(issue, board, Context) { Host = this });
                    gadget.Note = gadget.Issues.Count == 0 ? "Nothing open is assigned to you." : null;
                    Gadgets.Add(gadget);
                    break;
                }
                case "pie":
                {
                    var name = PieFields.FirstOrDefault(f => f.Id == config.Setting)?.Name ?? config.Setting;
                    var gadget = new GadgetViewModel(i, $"Open issues by {name.ToLowerInvariant()}", this) { IsChart = true };
                    gadget.Chart = BoardReports.CountBy(board, board.Issues.Values.Where(x => !board.IsDone(x) && board.LevelOf(x) != IssueTypeLevel.Epic), config.Setting);
                    Gadgets.Add(gadget);
                    break;
                }
                case "filter":
                {
                    var saved = board.Filters.GetValueOrDefault(config.Setting);
                    var gadget = new GadgetViewModel(i, saved?.Name ?? "A saved filter that was deleted", this);
                    if (saved is not null)
                    {
                        try
                        {
                            foreach (var issue in QueryCompiler.Compile(saved.Query, context).Run(board.Issues.Values).Take(20))
                                gadget.Issues.Add(new IssueItemViewModel(issue, board, Context) { Host = this });
                            gadget.Note = gadget.Issues.Count == 0 ? "No issue matches." : null;
                        }
                        catch (QueryException e)
                        {
                            gadget.Note = "The filter has a problem: " + e.Message;
                        }
                    }
                    Gadgets.Add(gadget);
                    break;
                }
            }
        }
    }

    private static readonly IReadOnlyList<NamedItem> PieFields =
    [
        new(IssueFields.Status, "Status"), new(IssueFields.Type, "Type"), new(IssueFields.Priority, "Priority"),
        new(IssueFields.Assignee, "Assignee"), new(IssueFields.Labels, "Label"), new(IssueFields.Components, "Component"), new("epic", "Epic"),
    ];

    [RelayCommand]
    private async Task AddGadgetAsync()
    {
        var filters = Context.Snapshot.Filters.Values.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var kinds = new List<string> { "Assigned to me", "Open issues by a field" };
        if (filters.Count > 0)
            kinds.Add("A saved filter's results");
        var dialog = new TextInputDialogViewModel("Add Gadget", "", "", "Add")
        {
            ShowText = false,
            Choice = new ChoiceInput("Gadget", kinds, kinds[1]),
            SecondChoice = new ChoiceInput("Field or filter", [.. PieFields.Select(f => f.Name), .. filters.Select(f => "Filter: " + f.Name)], PieFields[0].Name),
            Explanation = "\"Open issues by a field\" uses the field; a saved filter's results use the filter (save one on the Search page).",
        };
        if (!await Context.Dialogs.ShowDialogAsync(dialog))
            return;
        var choice = dialog.SecondChoice!.Selected;
        var config = dialog.Choice!.Selected switch
        {
            "Assigned to me" => new GadgetConfig { Kind = "assigned" },
            "A saved filter's results" => new GadgetConfig
            {
                Kind = "filter",
                Setting = filters.FirstOrDefault(f => "Filter: " + f.Name == choice)?.Id ?? filters[0].Id,
            },
            _ => new GadgetConfig { Kind = "pie", Setting = PieFields.FirstOrDefault(f => f.Name == choice)?.Id ?? IssueFields.Status },
        };
        var configs = Configs();
        configs.Add(config);
        SaveConfigs(configs);
        Refresh();
    }

    internal void RemoveGadget(int index)
    {
        var configs = Configs();
        if (index < 0 || index >= configs.Count)
            return;
        configs.RemoveAt(index);
        SaveConfigs(configs);
        Refresh();
    }

    [RelayCommand]
    private void SelectIssue(IssueItemViewModel? item) => Host.SelectIssue(item?.Id);

    [RelayCommand]
    private void OpenIssue(IssueItemViewModel? item)
    {
        if (item is not null)
            Host.OpenIssue(item.Id);
    }
}
