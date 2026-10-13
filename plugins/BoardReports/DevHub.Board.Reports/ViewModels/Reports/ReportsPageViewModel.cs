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
using DevHub.Sdk.Core.Charts;
using DevHub.Sdk.Core.Settings;
using DevHub.ViewModels.Board;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Reports;

/// <summary>The Reports page of every board.</summary>
public sealed class ReportsPageProvider(ISettingsStore settings) : IBoardPageProvider
{
    public string Id => "reports";
    public string Title => "Reports";
    public int Order => 700;
    public BoardPageViewModel Create(BoardPageHost host) => new ReportsPageViewModel(host, settings);
}

/// <summary>A report in the list on the left.</summary>
public sealed record ReportKind(string Id, string Name, string Description, bool NeedsSprint)
{
    public override string ToString() => Name;
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

/// <summary>A row of the sprint report or epic progress.</summary>
public sealed record ReportRow(string Key, string Summary, string Detail, double Fraction)
{
    public override string ToString() => $"{Key} {Summary} {Detail}";
}

/// <summary>
/// Reports for the board: pick one on the left (sprint reports pick a sprint), the chart and its numbers on the right.
/// The Dashboard is a column of gadgets you add and remove (kept per board in Board.Reports' settings).
/// </summary>
public sealed partial class ReportsPageViewModel : BoardPageViewModel, ICardHost
{
    private readonly ISettingsStore _settings;

    public ReportsPageViewModel(BoardPageHost host, ISettingsStore settings) : base(host)
    {
        _settings = settings;
        Kinds =
        [
            new("dashboard", "Dashboard", "Gadgets you choose: your issues, issues by a field, saved searches.", false),
            new("burndown", "Burndown", "Points left in a sprint, day by day, against the ideal line.", true),
            new("burnup", "Burnup", "Points done and the sprint's scope, day by day: scope changes show.", true),
            new("sprint", "Sprint report", "What a sprint finished, what it didn't, what was added and removed.", true),
            new("velocity", "Velocity", "Points committed and completed in the last sprints.", false),
            new("flow", "Cumulative flow", "Issues per status category each day of the last 30 days.", false),
            new("control", "Control chart", "Cycle time (first In Progress to resolved) of issues resolved in the last 90 days.", false),
            new("epics", "Epic progress", "Every epic's done and remaining issues and points.", false),
        ];
        _kind = Kinds[1];
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

    [ObservableProperty] private ChartData _chart = ChartData.Empty;
    [ObservableProperty] private bool _isLine;
    [ObservableProperty] private bool _isBar;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _empty;
    public ObservableCollection<ReportRow> Rows { get; } = [];
    public bool HasRows => Rows.Count > 0;
    [ObservableProperty] private string _rowsTitle = "";

    public ObservableCollection<GadgetViewModel> Gadgets { get; } = [];

    public override void OnBoardChanged() => Refresh();

    partial void OnKindChanged(ReportKind value) => Refresh();
    partial void OnSprintChanged(Sprint? value)
    {
        if (!_refreshing)
            Refresh();
    }

    private bool _refreshing;

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
            Rows.Clear();
            Empty = null;
            Chart = ChartData.Empty;
            IsLine = IsBar = false;
            Summary = Kind.Description;

            OnPropertyChanged(nameof(HasRows));
            switch (Kind.Id)
            {
                case "dashboard":
                    BuildDashboard(board);
                    break;
                case "burndown" or "burnup" or "sprint" when Sprint is null:
                    Empty = "No sprint has started yet (sprints come with the Board.Scrum plugin).";
                    break;
                case "burndown" or "burnup":
                {
                    var chart = BoardReports.Sprint(board, Sprint!, now);
                    Chart = Kind.Id == "burndown" ? chart.Burndown : chart.Burnup;
                    IsLine = true;
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
                    Chart = BoardReports.Velocity(board);
                    IsBar = true;
                    Summary = BoardReports.AverageVelocity(board) is { } v
                        ? $"Average of the last sprints: {N(v)} points"
                        : "No sprint has been completed yet.";
                    if (Chart.Labels.Count == 0)
                        Empty = "Velocity appears after the first completed sprint.";
                    break;
                case "flow":
                    Chart = BoardReports.CumulativeFlow(board, now);
                    IsLine = true;
                    break;
                case "control":
                {
                    var cycles = BoardReports.CycleTimes(board, now);
                    Chart = BoardReports.ControlChart(cycles);
                    IsLine = true;
                    Summary = cycles.Count == 0 ? "No issue was resolved in the last 90 days."
                        : $"{cycles.Count} issues · average {cycles.Average(c => c.Days):0.#} days · median {Median(cycles.Select(c => c.Days)):0.#} days";
                    if (cycles.Count == 0)
                        Empty = Summary;
                    break;
                }
                case "epics":
                    RowsTitle = "Epics";
                    foreach (var e in BoardReports.Epics(board))
                        Rows.Add(new ReportRow(e.Epic.Key, e.Epic.Summary,
                            $"{e.DoneIssues}/{e.Issues} issues · {N(e.DonePoints)}/{N(e.Points)} points", e.Fraction));
                    if (Rows.Count == 0)
                        Empty = "The board has no epics.";
                    break;
            }
        }
        finally
        {
            OnPropertyChanged(nameof(HasRows));
            _refreshing = false;
        }
    }

    private static string N(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
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
