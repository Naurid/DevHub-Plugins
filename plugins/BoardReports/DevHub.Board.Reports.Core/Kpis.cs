using System.Globalization;
using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Sdk.Core.Charts;
using DevHub.Sdk.Core.Tables;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Reports.Core;

/// <summary>A change to a scope: an issue added, re-estimated or dropped, with the points it moved.</summary>
public sealed record ScopeChange(Issue Issue, string What, double Points, DateTimeOffset At);

/// <summary>
/// Indicators for steering a Scrum project, per closed sprint unless said otherwise. Each has a short explanation of
/// what it shows and what a team does about it (<see cref="Explain"/>), for the report's interpretation.
/// </summary>
public static class Kpis
{
    /// <summary>Closed sprints, oldest first (the last <paramref name="count"/>).</summary>
    public static IReadOnlyList<Sprint> Closed(BoardSnapshot board, int count = 12) =>
        board.Sprints.Values.Where(s => s.State == SprintState.Closed).OrderBy(s => s.Completed ?? s.End).TakeLast(count).ToList();

    /// <summary>Completed ÷ committed points per sprint, in percent, with 100% as a reference line.</summary>
    public static ChartData CommitmentReliability(BoardSnapshot board)
    {
        var sprints = Closed(board);
        return new ChartData(sprints.Select(s => s.Name).ToList(),
            [new ChartSeries(L.T("Completed / committed"), sprints.Select(s => s.CommittedPoints > 0 ? (double?)Math.Round(100 * s.CompletedPoints / s.CommittedPoints) : null).ToList())
                { Style = new SeriesStyle { ValueLabels = true } }])
        {
            Title = L.T("Commitment reliability"),
            YTitle = "%",
            ValueSuffix = "%",
            References = [new ChartReference(L.T("Commitment"), 100)],
        };
    }

    /// <summary>Issues finished per sprint (standard issues), and their points.</summary>
    public static ChartData Throughput(BoardSnapshot board)
    {
        var sprints = Closed(board);
        return new ChartData(sprints.Select(s => s.Name).ToList(),
        [
            new ChartSeries(L.T("Issues done"), sprints.Select(s => (double?)s.CompletedIssues.Count).ToList()) { Style = new SeriesStyle { Kind = SeriesKind.Bar, ValueLabels = true } },
            new ChartSeries(L.T("Points done"), sprints.Select(s => (double?)s.CompletedPoints).ToList()) { Style = new SeriesStyle { Markers = true } },
        ]) { Title = L.T("Throughput"), XTitle = L.T("Sprint") };
    }

    /// <summary>Lead time (created to resolved, days) of issues resolved in the last <paramref name="days"/> days, oldest first.</summary>
    public static IReadOnlyList<(Issue Issue, double Days)> LeadTimes(BoardSnapshot board, DateTimeOffset now, int days = 90) =>
        board.Issues.Values
            .Where(i => board.IsDone(i) && i.Resolved is { } r && r >= now.AddDays(-days) && board.LevelOf(i) != IssueTypeLevel.Epic)
            .OrderBy(i => i.Resolved)
            .Select(i => (i, Math.Round((i.Resolved!.Value - i.Created).TotalDays, 1)))
            .ToList();

    public static ChartData LeadTimeChart(IReadOnlyList<(Issue Issue, double Days)> leads)
    {
        var average = leads.Count == 0 ? 0 : leads.Average(l => l.Days);
        return new ChartData(leads.Select(l => l.Issue.Key).ToList(),
            [new ChartSeries(L.T("Lead time"), leads.Select(l => (double?)l.Days).ToList()) { Style = new SeriesStyle { Markers = true } }])
        {
            Title = L.T("Lead time (created → done)"),
            YTitle = L.T("Days"),
            ValueSuffix = " d",
            References = leads.Count == 0 ? [] : [new ChartReference(L.T("Average"), Math.Round(average, 1))],
        };
    }

    /// <summary>Bugs found (created) and fixed (resolved) during each sprint, and the share of bugs among issues done.</summary>
    public static ChartData Defects(BoardSnapshot board)
    {
        var sprints = Closed(board).Where(s => s.Start is not null).ToList();
        var bugs = board.Issues.Values.Where(i => i.Type == "bug" || board.Project.IssueType(i.Type)?.Icon == "bug").ToList();
        bool In(Sprint s, DateTimeOffset at) => at >= s.Start && at <= (s.Completed ?? s.End ?? DateTimeOffset.MaxValue);
        return new ChartData(sprints.Select(s => s.Name).ToList(),
        [
            new ChartSeries(L.T("Bugs found"), sprints.Select(s => (double?)bugs.Count(b => In(s, b.Created))).ToList()) { Style = new SeriesStyle { Kind = SeriesKind.Bar, Color = 3 } },
            new ChartSeries(L.T("Bugs fixed"), sprints.Select(s => (double?)bugs.Count(b => b.Resolved is { } r && In(s, r))).ToList()) { Style = new SeriesStyle { Kind = SeriesKind.Bar, Color = 1 } },
        ]) { Title = L.T("Defects per sprint"), XTitle = L.T("Sprint") };
    }

    /// <summary>Issues in progress at the end of each of the last <paramref name="days"/> days.</summary>
    public static ChartData WorkInProgress(BoardSnapshot board, DateTimeOffset now, int days = 30)
    {
        var flow = BoardReports.CumulativeFlow(board, now, days);
        var wip = flow.Series.FirstOrDefault(s => s.Name == L.T("In Progress"));
        return wip is null ? ChartData.Empty : new ChartData(flow.Labels, [wip with { Name = L.T("In progress") }])
        {
            Title = L.T("Work in progress"),
            YTitle = L.T("Issues"),
            References = wip.Values.Any(v => v is not null) ? [new ChartReference(L.T("Average"), Math.Round(wip.Values.Average(v => v ?? 0), 1))] : [],
        };
    }

    /// <summary>Every sprint so far and planned: capacity (bars), committed and completed points (lines).</summary>
    public static ChartData CapacityLoad(BoardSnapshot board)
    {
        var sprints = Ordered(board);
        return new ChartData(sprints.Select(s => s.Name).ToList(),
        [
            new ChartSeries(L.T("Capacity"), sprints.Select(s => s.Capacity).ToList()) { Style = new SeriesStyle { Kind = SeriesKind.Bar, Color = 0 } },
            new ChartSeries(L.T("Planned / committed"), sprints.Select(s => (double?)(s.State == SprintState.Future ? Planned(board, s) : s.CommittedPoints)).ToList())
                { Style = new SeriesStyle { Markers = true, Color = 2 } },
            new ChartSeries(L.T("Completed"), sprints.Select(s => s.State == SprintState.Closed ? (double?)s.CompletedPoints : null).ToList())
                { Style = new SeriesStyle { Markers = true, Color = 1 } },
        ]) { Title = L.T("Capacity, load and completed"), XTitle = L.T("Sprint"), YTitle = L.T("Story points") };
    }

    /// <summary>Sprints in time: closed, the running one, then the planned ones in their order.</summary>
    public static IReadOnlyList<Sprint> Ordered(BoardSnapshot board) =>
        board.Sprints.Values
            .OrderBy(s => s.State switch { SprintState.Closed => 0, SprintState.Active => 1, _ => 2 })
            .ThenBy(s => s.State == SprintState.Future ? (DateTimeOffset?)null : s.Start)
            .ThenBy(s => s.Sequence)
            .ToList();

    /// <summary>The points planned in a sprint now (standard issues).</summary>
    public static double Planned(BoardSnapshot board, Sprint sprint) =>
        board.Issues.Values.Where(i => i.Sprint == sprint.Id && board.LevelOf(i) == IssueTypeLevel.Standard).Sum(i => i.StoryPoints ?? 0);

    /// <summary>
    /// The plan of every sprint as a table (the "sprint planning" annex): dates, goal, capacity, team days, planned or
    /// committed and completed points, and its stories.
    /// </summary>
    public static TableData SprintPlans(BoardSnapshot board)
    {
        string D(DateTimeOffset? d) => d?.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        string N(double? v) => v?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        var rows = Ordered(board).Select(s =>
        {
            var issues = board.Issues.Values.Where(i => i.Sprint == s.Id && board.LevelOf(i) == IssueTypeLevel.Standard)
                .OrderBy(i => i.Rank, StringComparer.Ordinal).ToList();
            return (IReadOnlyList<string>)
            [
                s.Name, s.State.ToString(), D(s.Start), D(s.End), s.Goal, N(s.Capacity), N(s.PersonDays > 0 ? s.PersonDays : null),
                N(s.Availability.Count > 0 ? s.FocusedDays : null), N(s.State == SprintState.Future ? Planned(board, s) : s.CommittedPoints),
                s.State == SprintState.Closed ? N(s.CompletedPoints) : "",
                string.Join("; ", issues.Select(i => $"{i.Key} {i.Summary} ({N(i.StoryPoints)})")),
            ];
        }).ToList();
        return new TableData([L.T("Sprint"), L.T("State"), L.T("Start"), L.T("End"), L.T("Goal"), L.T("Capacity"), "Person-days", L.T("Focused days"),
            L.T("Planned / committed"), L.T("Completed"), L.T("Stories")], rows) { Title = L.T("Sprint plans") };
    }

    /// <summary>
    /// What changed in a scope from <paramref name="from"/> to <paramref name="to"/>: issues added (created then, with
    /// their points), re-estimated (points changed) and dropped (resolved as anything but done: won't do, duplicate).
    /// </summary>
    public static IReadOnlyList<ScopeChange> ScopeChanges(BoardSnapshot board, IReadOnlyList<Issue> scope, DateTimeOffset from, DateTimeOffset to)
    {
        var changes = new List<ScopeChange>();
        foreach (var issue in scope.Where(i => board.LevelOf(i) == IssueTypeLevel.Standard))
        {
            var history = board.HistoryOf(issue.Id);
            if (issue.Created >= from && issue.Created <= to)
            {
                var timeline = new IssueTimeline(issue, history);
                changes.Add(new ScopeChange(issue, "added", timeline.PointsAt(issue.Created.AddSeconds(1)) is > 0 and var p ? p : issue.StoryPoints ?? 0, issue.Created));
            }
            foreach (var h in history.Where(h => h.Field == IssueFields.StoryPoints && h.At >= from && h.At <= to && issue.Created < h.At.AddSeconds(-1)))
            {
                var before = double.TryParse(h.From, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : 0;
                var after = double.TryParse(h.To, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : 0;
                if (Math.Abs(after - before) > 1e-9 && issue.Created < from)
                    changes.Add(new ScopeChange(issue, L.T($"re-estimated {before:0.##} → {after:0.##}"), after - before, h.At));
            }
            if (issue.Resolved is { } r && r >= from && r <= to && issue.Resolution is { } res && res != "done")
                changes.Add(new ScopeChange(issue, L.T("dropped (") + (board.Project.Resolution(res)?.Name ?? res) + ")", -(issue.StoryPoints ?? 0), r));
        }
        return changes.OrderBy(c => c.At).ToList();
    }

    /// <summary>Points added and removed per epic (and "No epic"), for the scope change chart.</summary>
    public static ChartData ScopeChangeChart(BoardSnapshot board, IReadOnlyList<ScopeChange> changes)
    {
        var groups = changes.GroupBy(c => board.EpicOf(c.Issue)?.Summary ?? L.T("No epic")).OrderBy(g => g.Key).ToList();
        return new ChartData(groups.Select(g => g.Key).ToList(),
        [
            new ChartSeries(L.T("Points added"), groups.Select(g => (double?)g.Where(c => c.Points > 0).Sum(c => c.Points)).ToList()) { Style = new SeriesStyle { Color = 3 } },
            new ChartSeries(L.T("Points removed"), groups.Select(g => (double?)g.Where(c => c.Points < 0).Sum(c => -c.Points)).ToList()) { Style = new SeriesStyle { Color = 1 } },
        ]) { Title = L.T("Scope change by epic"), YTitle = L.T("Story points") };
    }

    /// <summary>What a report shows and what a team does with it (the report's interpretation and steering).</summary>
    public static string Explain(string report) => report switch
    {
        "velocity" => L.T("Points completed per sprint. Plan the next sprints with the average; a dip after a change in the team is normal while it settles."),
        "reliability" => L.T("How much of what the team committed to it finished. Far below 100%: commit to less or split stories; always above: the team can take more."),
        "throughput" => L.T("How many issues the team finishes per sprint. Steady numbers make forecasts reliable; big swings point at stories of very different sizes."),
        "leadtime" => L.T("How long an issue takes from being written to done. Long or growing lead times: the backlog holds work too early; refine closer to the work."),
        "defects" => L.T("Bugs found and fixed in each sprint. More found than fixed for several sprints: raise the Definition of Done (tests, reviews) or plan time for bugs."),
        "wip" => L.T("Issues in progress at the same time. High WIP slows everything down: finish before starting, or set WIP limits on the board's columns."),
        "capacity" => L.T("Capacity planned, points taken on and points completed. Committing above capacity or completing well below it are signs to re-plan."),
        "control" => L.T("Cycle time from starting an issue to finishing it. Points far above the average are issues that got stuck: look at what blocked them."),
        "flow" => L.T("Issues per status over time. A widening In Progress band means work starts faster than it finishes."),
        "forecast" => L.T("When the remaining scope can be done at a pessimistic, neutral and optimistic velocity. Late against the target: cut scope, add capacity or move the date."),
        "scope" => L.T("Issues added, re-estimated or dropped over a period. Large additions after planning push the delivery date: decide what leaves the scope in exchange."),
        "burndown" => L.T("Points left in the sprint against the ideal line. Flat stretches mean work is stuck; above the line near the end, the sprint goal is at risk."),
        "burnup" => L.T("Points done and the sprint's scope. A rising scope line means work was added during the sprint."),
        _ => "",
    };
}
