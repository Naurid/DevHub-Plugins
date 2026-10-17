using System.Globalization;
using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Sdk.Core.Charts;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Reports.Core;

/// <summary>
/// An issue's fields at any moment in the past, from its current values and its change log (walking back undoes each
/// change made after that moment). Before it was created it didn't exist.
/// </summary>
public sealed class IssueTimeline
{
    private readonly Issue _issue;
    private readonly IReadOnlyList<HistoryEntry> _history;

    public IssueTimeline(Issue issue, IReadOnlyList<HistoryEntry> history)
    {
        _issue = issue;
        _history = history.OrderBy(h => h.At).ToList();
    }

    public Issue Issue => _issue;

    public bool ExistedAt(DateTimeOffset when) => _issue.Created <= when;

    /// <summary>The raw value of <paramref name="field"/> at <paramref name="when"/> (as history stores it).</summary>
    public string? ValueAt(string field, DateTimeOffset when)
    {
        var value = IssueFields.Get(_issue, field).Raw;
        for (var i = _history.Count - 1; i >= 0 && _history[i].At > when; i--)
        {
            if (_history[i].Field == field)
                value = _history[i].From;
        }
        return value;
    }

    public string? StatusAt(DateTimeOffset when) => ValueAt(IssueFields.Status, when);
    public string? SprintAt(DateTimeOffset when) => ValueAt(IssueFields.Sprint, when);

    public double PointsAt(DateTimeOffset when) =>
        double.TryParse(ValueAt(IssueFields.StoryPoints, when), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;

    /// <summary>When it last entered a status of <paramref name="category"/> (null: never, or not now).</summary>
    public DateTimeOffset? FirstEntered(StatusCategory category, BoardProject project) =>
        _history.Where(h => h.Field == IssueFields.Status && project.Status(h.To)?.Category == category).Select(h => (DateTimeOffset?)h.At).FirstOrDefault()
        ?? (project.Status(_issue.Status)?.Category == category && !_history.Any(h => h.Field == IssueFields.Status) ? _issue.Created : null);
}

/// <summary>A sprint's burndown and burnup, day by day.</summary>
public sealed record SprintChart(ChartData Burndown, ChartData Burnup, double Committed, double Completed, double Scope);

/// <summary>What happened to a sprint's issues.</summary>
public sealed record SprintReport(
    Sprint Sprint,
    IReadOnlyList<Issue> Completed,
    IReadOnlyList<Issue> NotCompleted,
    IReadOnlyList<Issue> Added,
    IReadOnlyList<Issue> Removed,
    double CommittedPoints,
    double CompletedPoints);

/// <summary>An epic's progress.</summary>
public sealed record EpicProgress(Issue Epic, int Issues, int DoneIssues, double Points, double DonePoints)
{
    public double Fraction => Points > 0 ? DonePoints / Points : Issues > 0 ? (double)DoneIssues / Issues : 0;
}

/// <summary>An issue's cycle time: from first In Progress to resolved, in days.</summary>
public sealed record CycleTime(Issue Issue, DateTimeOffset Started, DateTimeOffset Resolved)
{
    public double Days => (Resolved - Started).TotalDays;
}

/// <summary>
/// The reports (docs/BOARD_PLAN.md, B9), worked out from a snapshot. Days are local calendar days; "now" is passed in
/// so tests can fix it.
/// </summary>
public static class BoardReports
{
    public static IReadOnlyList<IssueTimeline> Timelines(BoardSnapshot board) =>
        board.Issues.Values.Select(i => new IssueTimeline(i, board.HistoryOf(i.Id))).ToList();

    private static bool DoneAt(IssueTimeline t, DateTimeOffset when, BoardProject project) =>
        project.Status(t.StatusAt(when))?.Category == StatusCategory.Done;

    private static IEnumerable<DateTimeOffset> Days(DateTimeOffset start, DateTimeOffset end)
    {
        var day = new DateTimeOffset(start.LocalDateTime.Date, start.Offset);
        for (; day <= end; day = day.AddDays(1))
            yield return day.AddDays(1).AddTicks(-1) < end ? day.AddDays(1).AddTicks(-1) : end;   // end of each day (or now)
    }

    /// <summary>
    /// Burndown (points left, with the ideal line) and burnup (done vs scope) of a sprint, from its start to its end
    /// (or now, while it runs). Counts standard issues (sub-tasks count through their parent), as the sprint was then.
    /// </summary>
    public static SprintChart Sprint(BoardSnapshot board, Sprint sprint, DateTimeOffset now)
    {
        if (sprint.Start is not { } start)
            return new SprintChart(ChartData.Empty, ChartData.Empty, 0, 0, 0);
        var plannedEnd = sprint.End ?? start.AddDays(14);
        var end = sprint.Completed ?? (now < plannedEnd ? now : plannedEnd);
        var project = board.Project;
        var timelines = Timelines(board).Where(t => board.LevelOf(t.Issue) == IssueTypeLevel.Standard).ToList();
        var labels = new List<string>();
        var remaining = new List<double?>();
        var ideal = new List<double?>();
        var done = new List<double?>();
        var scope = new List<double?>();
        var committed = sprint.Committed.Count > 0 ? sprint.CommittedPoints
            : timelines.Where(t => t.SprintAt(start) == sprint.Id).Sum(t => t.PointsAt(start));
        var allDays = Days(start, plannedEnd).ToList();
        for (var i = 0; i < allDays.Count; i++)
        {
            var day = allDays[i];
            labels.Add(day.LocalDateTime.ToString("d MMM", CultureInfo.CurrentCulture));
            ideal.Add(allDays.Count == 1 ? committed : committed * (1 - (double)i / (allDays.Count - 1)));
            if (day > end && day.Date > end.Date)
            {
                remaining.Add(null);
                done.Add(null);
                scope.Add(null);
                continue;
            }
            var at = day > end ? end : day;
            var inSprint = timelines.Where(t => t.ExistedAt(at) && t.SprintAt(at) == sprint.Id).ToList();
            var total = inSprint.Sum(t => t.PointsAt(at));
            var finished = inSprint.Where(t => DoneAt(t, at, project)).Sum(t => t.PointsAt(at));
            remaining.Add(total - finished);
            done.Add(finished);
            scope.Add(total);
        }
        var burndown = new ChartData(labels, [new ChartSeries(L.T("Remaining"), remaining), new ChartSeries(L.T("Ideal"), ideal)]);
        var burnup = new ChartData(labels, [new ChartSeries(L.T("Done"), done), new ChartSeries(L.T("Scope"), scope)]);
        return new SprintChart(burndown, burnup, committed, done.LastOrDefault(d => d is not null) ?? 0, scope.LastOrDefault(s => s is not null) ?? 0);
    }

    /// <summary>Committed vs completed points of the last <paramref name="count"/> closed sprints, oldest first.</summary>
    public static ChartData Velocity(BoardSnapshot board, int count = 8)
    {
        var sprints = board.Sprints.Values.Where(s => s.State == SprintState.Closed).OrderBy(s => s.Completed ?? s.End).TakeLast(count).ToList();
        return new ChartData(sprints.Select(s => s.Name).ToList(),
            [new ChartSeries(L.T("Committed"), sprints.Select(s => (double?)s.CommittedPoints).ToList()),
             new ChartSeries(L.T("Completed"), sprints.Select(s => (double?)s.CompletedPoints).ToList())]);
    }

    /// <summary>Average completed points of the last three closed sprints (null: none yet).</summary>
    public static double? AverageVelocity(BoardSnapshot board)
    {
        var last = board.Sprints.Values.Where(s => s.State == SprintState.Closed).OrderBy(s => s.Completed ?? s.End).TakeLast(3).ToList();
        return last.Count == 0 ? null : last.Average(s => s.CompletedPoints);
    }

    /// <summary>
    /// Cumulative flow: issues per status category at the end of each of the last <paramref name="days"/> days (or per
    /// status with <paramref name="byStatus"/>), standard issues only.
    /// </summary>
    public static ChartData CumulativeFlow(BoardSnapshot board, DateTimeOffset now, int days = 30, bool byStatus = false)
    {
        var project = board.Project;
        var timelines = Timelines(board).Where(t => board.LevelOf(t.Issue) == IssueTypeLevel.Standard).ToList();
        var dayEnds = Days(now.AddDays(-(days - 1)), now).ToList();
        var labels = dayEnds.Select(d => d.LocalDateTime.ToString("d MMM", CultureInfo.CurrentCulture)).ToList();
        IReadOnlyList<(string Name, Func<string?, bool> Match)> groups = byStatus
            ? (board.DefaultWorkflow?.Statuses ?? project.Statuses.Select(s => s.Id).ToList())
                .Select(id => (project.Status(id)?.Name ?? id, (Func<string?, bool>)(s => s == id))).ToList()
            : [(L.T("Done"), s => project.Status(s)?.Category == StatusCategory.Done),
               (L.T("In Progress"), s => project.Status(s)?.Category == StatusCategory.InProgress),
               (L.T("To Do"), s => project.Status(s)?.Category is StatusCategory.ToDo or null)];
        var series = groups.Select(g => new ChartSeries(g.Name,
            dayEnds.Select(d => (double?)timelines.Count(t => t.ExistedAt(d) && g.Match(t.StatusAt(d)))).ToList())).ToList();
        return new ChartData(labels, series);
    }

    /// <summary>Cycle times of issues resolved in the last <paramref name="days"/> days, oldest first.</summary>
    public static IReadOnlyList<CycleTime> CycleTimes(BoardSnapshot board, DateTimeOffset now, int days = 90)
    {
        var project = board.Project;
        return Timelines(board)
            .Where(t => board.IsDone(t.Issue) && t.Issue.Resolved is { } r && r >= now.AddDays(-days) && board.LevelOf(t.Issue) != IssueTypeLevel.Epic)
            .Select(t => new CycleTime(t.Issue, t.FirstEntered(StatusCategory.InProgress, project) ?? t.Issue.Created, t.Issue.Resolved!.Value))
            .OrderBy(c => c.Resolved).ToList();
    }

    /// <summary>The control chart: each resolved issue's cycle time (days), with the running average.</summary>
    public static ChartData ControlChart(IReadOnlyList<CycleTime> cycles)
    {
        var labels = cycles.Select(c => c.Issue.Key).ToList();
        var values = cycles.Select(c => (double?)Math.Round(c.Days, 1)).ToList();
        var average = new List<double?>();
        double sum = 0;
        for (var i = 0; i < cycles.Count; i++)
        {
            sum += cycles[i].Days;
            average.Add(Math.Round(sum / (i + 1), 1));
        }
        return new ChartData(labels, [new ChartSeries(L.T("Cycle time (days)"), values), new ChartSeries(L.T("Average"), average)]);
    }

    /// <summary>A sprint's outcome: done, not done, added after it started, removed during it.</summary>
    public static SprintReport SprintReportOf(BoardSnapshot board, Sprint sprint, DateTimeOffset now)
    {
        var project = board.Project;
        var timelines = Timelines(board).Where(t => board.LevelOf(t.Issue) == IssueTypeLevel.Standard).ToList();
        var start = sprint.Start ?? now;
        var end = sprint.Completed ?? now;
        var committedIds = sprint.Committed.Select(c => c.Issue).ToHashSet(StringComparer.Ordinal);
        var atEnd = timelines.Where(t => t.SprintAt(end) == sprint.Id || sprint.CompletedIssues.Any(c => c.Issue == t.Issue.Id)
                                         || sprint.NotCompleted.Any(c => c.Issue == t.Issue.Id)).ToList();
        var completed = atEnd.Where(t => DoneAt(t, end, project)).Select(t => t.Issue).ToList();
        var notCompleted = atEnd.Where(t => !DoneAt(t, end, project)).Select(t => t.Issue).ToList();
        var added = atEnd.Where(t => !committedIds.Contains(t.Issue.Id) && sprint.Start is not null).Select(t => t.Issue).ToList();
        var removed = timelines.Where(t => committedIds.Contains(t.Issue.Id) && atEnd.All(a => a.Issue.Id != t.Issue.Id)).Select(t => t.Issue).ToList();
        return new SprintReport(sprint, completed, notCompleted, added, removed,
            sprint.Committed.Count > 0 ? sprint.CommittedPoints : timelines.Where(t => t.SprintAt(start) == sprint.Id).Sum(t => t.PointsAt(start)),
            completed.Sum(i => i.StoryPoints ?? 0));
    }

    /// <summary>Every epic's progress (open ones first, by rank).</summary>
    public static IReadOnlyList<EpicProgress> Epics(BoardSnapshot board) =>
        board.Issues.Values.Where(i => board.LevelOf(i) == IssueTypeLevel.Epic)
            .OrderBy(board.IsDone).ThenBy(i => i.Rank, StringComparer.Ordinal)
            .Select(epic =>
            {
                var children = board.ChildrenOf(epic.Id);
                var done = children.Where(board.IsDone).ToList();
                return new EpicProgress(epic, children.Count, done.Count, children.Sum(c => c.StoryPoints ?? 0), done.Sum(c => c.StoryPoints ?? 0));
            }).ToList();

    /// <summary>Issues counted by a field's value (for a pie): status, type, priority, assignee, label, component, epic.</summary>
    public static ChartData CountBy(BoardSnapshot board, IEnumerable<Issue> issues, string field)
    {
        var project = board.Project;
        IEnumerable<string> Values(Issue i) => field switch
        {
            IssueFields.Status => [project.Status(i.Status)?.Name ?? i.Status],
            IssueFields.Type => [project.IssueType(i.Type)?.Name ?? i.Type],
            IssueFields.Priority => [project.Priority(i.Priority)?.Name ?? L.T("None")],
            IssueFields.Assignee => [project.Person(i.Assignee)?.Name ?? L.T("Unassigned")],
            IssueFields.Labels => i.Labels.Count == 0 ? [L.T("No label")] : i.Labels,
            IssueFields.Components => i.Components.Count == 0 ? [L.T("No component")] : i.Components.Select(c => project.Component(c)?.Name ?? c),
            "epic" => [board.EpicOf(i)?.Summary ?? L.T("No epic")],
            _ => ["?"],
        };
        var counts = issues.SelectMany(Values).GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCulture)
            .Select(g => (g.Key, (double)g.Count()));
        return ChartData.Of(L.T("Issues"), counts);
    }
}
