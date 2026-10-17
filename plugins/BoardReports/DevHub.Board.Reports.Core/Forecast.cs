using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using DevHub.Sdk.Core.Charts;

namespace DevHub.Board.Reports.Core;

/// <summary>The three velocities a forecast is made with (points per sprint).</summary>
public sealed record ScenarioVelocities(double Pessimistic, double Neutral, double Optimistic)
{
    /// <summary>
    /// From the last closed sprints (up to <paramref name="count"/>): their lowest, average and highest completed points;
    /// null before any sprint closed (a new team sets them by hand, or plans each sprint's velocity).
    /// </summary>
    public static ScenarioVelocities? FromHistory(BoardSnapshot board, int count = 3)
    {
        var done = board.Sprints.Values.Where(s => s.State == SprintState.Closed).OrderBy(s => s.Completed ?? s.End)
            .TakeLast(count).Select(s => s.CompletedPoints).ToList();
        return done.Count == 0 ? null : new ScenarioVelocities(done.Min(), Math.Round(done.Average(), 1), done.Max());
    }
}

/// <summary>One scenario's answer: how many sprints, which date, early or late against the target.</summary>
public sealed record ScenarioForecast(string Name, double Velocity, int? Sprints, DateOnly? Delivery, int? DaysLate)
{
    /// <summary>"3 sprints · 14 Nov 2026 · 5 days late" (or "never at this velocity").</summary>
    public string Text => Sprints is null
        ? "not within a year at this velocity"
        : $"{Sprints} sprint{(Sprints == 1 ? "" : "s")} · {Delivery:d MMM yyyy}" + DaysLate switch
        {
            null => "",
            0 => " · on the target date",
            > 0 and var late => $" · {late} days late",
            var early => $" · {-early} days early",
        };
}

/// <summary>A forecast: the scope, what's done, what's left, the three scenarios and the product burnup chart.</summary>
public sealed record Forecast(
    double ScopePoints,
    double DonePoints,
    int Unestimated,
    DateOnly NextSprintStart,
    int SprintDays,
    DateOnly? Target,
    IReadOnlyList<ScenarioForecast> Scenarios,
    ChartData Chart)
{
    public double Remaining => Math.Max(0, ScopePoints - DonePoints);
}

/// <summary>
/// Release forecasts from velocity: the remaining points of a scope (the backlog, a version, an epic, a filter's issues)
/// divided over future sprints at a pessimistic, a neutral and an optimistic velocity. A team that's still forming can
/// give planned velocities per sprint (lower at first); the scenarios then scale those by their ratio to the neutral one.
/// </summary>
public static class Forecasts
{
    /// <summary>The scenarios' names, in order.</summary>
    public static readonly IReadOnlyList<string> Names = ["Pessimistic", "Neutral", "Optimistic"];

    private const int MaxSprints = 26;

    public static Forecast Build(BoardSnapshot board, IReadOnlyList<Issue> scope, ScenarioVelocities velocities, DateOnly today,
        DateOnly? target = null, IReadOnlyList<double>? planned = null)
    {
        var standard = scope.Where(i => board.LevelOf(i) == IssueTypeLevel.Standard).ToList();
        var scopePoints = standard.Sum(i => i.StoryPoints ?? 0);
        var donePoints = standard.Where(board.IsDone).Sum(i => i.StoryPoints ?? 0);
        var unestimated = standard.Count(i => i.StoryPoints is null && !board.IsDone(i));
        var remaining = Math.Max(0, scopePoints - donePoints);
        var (start, days) = NextSprint(board, today);

        double PlannedAt(int sprint) => planned is { Count: > 0 } p && velocities.Neutral > 0
            ? p[Math.Min(sprint, p.Count - 1)]
            : velocities.Neutral;
        double VelocityAt(double scenario, int sprint) =>
            velocities.Neutral > 0 ? PlannedAt(sprint) * scenario / velocities.Neutral : scenario;

        var scenarios = new List<ScenarioForecast>();
        var cumulative = new List<double[]>();
        foreach (var (name, velocity) in Names.Zip([velocities.Pessimistic, velocities.Neutral, velocities.Optimistic]))
        {
            var sums = new double[MaxSprints + 1];
            int? needed = remaining <= 0 ? 0 : null;
            for (var k = 1; k <= MaxSprints; k++)
            {
                sums[k] = sums[k - 1] + Math.Max(0, VelocityAt(velocity, k - 1));
                if (needed is null && sums[k] >= remaining - 1e-9)
                    needed = k;
            }
            cumulative.Add(sums);
            DateOnly? delivery = needed is { } n ? (n == 0 ? today : start.AddDays(n * days - 1)) : null;
            int? late = delivery is { } d && target is { } t ? d.DayNumber - t.DayNumber : null;
            scenarios.Add(new ScenarioForecast(name, Math.Round(PlannedAt(0) * (velocities.Neutral > 0 ? velocity / velocities.Neutral : 1), 1), needed, delivery, late));
        }

        return new Forecast(scopePoints, donePoints, unestimated, start, days, target, scenarios,
            Burnup(board, standard, donePoints, scopePoints, scenarios, cumulative, start, days));
    }

    // When the next sprint starts and how many calendar days sprints last: after the running sprint (or today), as long
    // as the last sprint with dates (two weeks without one).
    private static (DateOnly Start, int Days) NextSprint(BoardSnapshot board, DateOnly today)
    {
        var dated = board.Sprints.Values.Where(s => s.Start is not null && s.End is not null).OrderByDescending(s => s.Start).ToList();
        // Calendar days, both ends included (Monday to the Sunday two weeks later: 14).
        var days = dated.Count == 0 ? 14 : Math.Max(1, (dated[0].End!.Value.LocalDateTime.Date - dated[0].Start!.Value.LocalDateTime.Date).Days + 1);
        var running = board.Sprints.Values.FirstOrDefault(s => s.State == SprintState.Active && s.End is not null);
        var start = running is not null ? DateOnly.FromDateTime(running.End!.Value.LocalDateTime).AddDays(1) : today;
        return (start < today ? today : start, days);
    }

    /// <summary>
    /// The product burnup: done and scope at the end of each closed sprint, now, then each scenario's projection (dashed)
    /// with the pessimistic–optimistic range shaded, up to the last scenario's delivery.
    /// </summary>
    private static ChartData Burnup(BoardSnapshot board, IReadOnlyList<Issue> scope, double done, double total,
        IReadOnlyList<ScenarioForecast> scenarios, IReadOnlyList<double[]> cumulative, DateOnly start, int days)
    {
        var ids = scope.Select(i => i.Id).ToHashSet();
        var timelines = BoardReports.Timelines(board).Where(t => ids.Contains(t.Issue.Id)).ToList();
        var closed = board.Sprints.Values.Where(s => s.State == SprintState.Closed && s.Completed is not null)
            .OrderBy(s => s.Completed).TakeLast(8).ToList();

        var labels = new List<string>();
        var doneSeries = new List<double?>();
        var scopeSeries = new List<double?>();
        foreach (var sprint in closed)
        {
            var at = sprint.Completed!.Value;
            labels.Add(sprint.Name);
            doneSeries.Add(timelines.Where(t => t.ExistedAt(at) && board.Project.Status(t.StatusAt(at))?.Category == StatusCategory.Done).Sum(t => t.PointsAt(at)));
            scopeSeries.Add(timelines.Where(t => t.ExistedAt(at)).Sum(t => t.PointsAt(at)));
        }
        labels.Add("Now");
        doneSeries.Add(done);
        scopeSeries.Add(total);
        var now = labels.Count - 1;

        var horizon = Math.Min(MaxSprints, Math.Max(1, scenarios.Max(s => s.Sprints ?? 8)));
        for (var k = 1; k <= horizon; k++)
        {
            labels.Add(start.AddDays(k * days - 1).ToString("d MMM", CultureInfo.CurrentCulture));
            doneSeries.Add(null);
            scopeSeries.Add(total);
        }

        var series = new List<ChartSeries>
        {
            new("Done", doneSeries) { Style = new SeriesStyle { Markers = true, Color = 1 } },
            new("Scope", scopeSeries) { Style = new SeriesStyle { Color = 0 } },
        };
        var projections = new List<List<double?>>();
        for (var s = 0; s < scenarios.Count; s++)
        {
            var values = Enumerable.Repeat<double?>(null, now).ToList();
            for (var k = 0; k <= horizon; k++)
                values.Add(Math.Min(total, done + cumulative[s][k]));
            projections.Add(values);
            series.Add(new ChartSeries(scenarios[s].Name, values)
            {
                Style = new SeriesStyle { Dash = s == 1 ? LineDash.Dashed : LineDash.Dotted, Color = s == 0 ? 3 : s == 1 ? 2 : 4 },
            });
        }
        return new ChartData(labels, series)
        {
            Title = "Release burnup and forecast",
            XTitle = "Sprint end",
            YTitle = "Story points",
            Bands = [new ChartBand("Pessimistic to optimistic", projections[0], projections[2])],
        };
    }
}

/// <summary>A forecast kept for later: to compare the dates before and after a change of scope.</summary>
public sealed record ForecastBaseline
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTimeOffset Taken { get; init; }

    /// <summary>Which scope it was taken for ("backlog", "version:…", "epic:…", "filter:…").</summary>
    public string Scope { get; init; } = "";

    public double ScopePoints { get; init; }
    public double DonePoints { get; init; }
    public IReadOnlyList<BaselineScenario> Scenarios { get; init; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public override string ToString() => Name;
}

public sealed record BaselineScenario
{
    public string Name { get; init; } = "";
    public double Velocity { get; init; }
    public int? Sprints { get; init; }
    public DateOnly? Delivery { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary><c>reports/baselines.json</c>: the board's saved forecasts, shared by everyone using it.</summary>
public static class Baselines
{
    public const string Path = "reports/baselines.json";

    public static IReadOnlyList<ForecastBaseline> All(BoardSnapshot board)
    {
        if (!board.Files.TryGetValue(Path, out var bytes))
            return [];
        try
        {
            return BoardJson.Deserialize<List<ForecastBaseline>>(bytes) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Baseline vs now, per scenario: "+12 days (14 Nov → 26 Nov)".</summary>
    public static IReadOnlyList<(string Scenario, string Before, string Now, int? DaysMoved)> Compare(ForecastBaseline baseline, Forecast now) =>
        Forecasts.Names.Select(name =>
        {
            var before = baseline.Scenarios.FirstOrDefault(s => s.Name == name);
            var after = now.Scenarios.FirstOrDefault(s => s.Name == name);
            int? moved = before?.Delivery is { } b && after?.Delivery is { } a ? a.DayNumber - b.DayNumber : null;
            return (name, before?.Delivery?.ToString("d MMM yyyy", CultureInfo.CurrentCulture) ?? "—",
                after?.Delivery?.ToString("d MMM yyyy", CultureInfo.CurrentCulture) ?? "—", moved);
        }).ToList();
}

/// <summary>Saves the forecast as a baseline (under a name, for its scope).</summary>
public sealed record SaveBaseline(string Name, string Scope, Forecast Forecast) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var name = Name.Trim().Length == 0 ? throw new BoardRuleException("Give the baseline a name.") : Name.Trim();
        var change = new ChangeBuilder(board, context);
        var baseline = new ForecastBaseline
        {
            Id = change.NewId(),
            Name = name,
            Taken = context.Now,
            Scope = Scope,
            ScopePoints = Forecast.ScopePoints,
            DonePoints = Forecast.DonePoints,
            Scenarios = Forecast.Scenarios.Select(s => new BaselineScenario { Name = s.Name, Velocity = s.Velocity, Sprints = s.Sprints, Delivery = s.Delivery }).ToList(),
        };
        change.Write(Baselines.Path, (List<ForecastBaseline>)[.. Baselines.All(board), baseline]);
        return change.Build($"Baseline \"{name}\" saved");
    }
}

public sealed record DeleteBaseline(string BaselineId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var all = Baselines.All(board);
        var gone = all.FirstOrDefault(b => b.Id == BaselineId) ?? throw new BoardRuleException("That baseline doesn't exist any more.");
        var change = new ChangeBuilder(board, context);
        change.Write(Baselines.Path, all.Where(b => b.Id != BaselineId).ToList());
        return change.Build($"Baseline \"{gone.Name}\" deleted");
    }
}
