using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using DevHub.Board.Reports.Core;
using Xunit;

namespace DevHub.Board.Reports.Core.Tests;

/// <summary>A clock tests move by hand.</summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A board in a temp folder with a manual clock (in local time, so days are calendar days here).</summary>
public sealed class ReportBoard : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "devhub-reports-" + Guid.NewGuid().ToString("N"));
    public static readonly DateTimeOffset Start = new(2026, 10, 5, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 5)));

    public ReportBoard()
    {
        Time = new ManualTime(Start);
        Session = BoardSession.CreateAsync(new FolderBoardStore(_folder), new CreateBoard("DEV", "Dev", BoardPreset.Scrum, new BoardUser("Ada")), Time)
            .GetAwaiter().GetResult();
    }

    public ManualTime Time { get; }
    public BoardSession Session { get; }
    public BoardSnapshot Snapshot => Session.Snapshot;

    public void Apply(IBoardOperation op) => Session.ApplyAsync(op).GetAwaiter().GetResult();

    public string Story(string summary, double points)
    {
        var id = BoardIds.New();
        Apply(new CreateIssue("story", summary) { Id = id, Fields = [(IssueFields.StoryPoints, new FieldValue.Number(points))] });
        return id;
    }

    public void Advance(double days) => Time.Now = Time.Now.AddDays(days);

    public void Dispose()
    {
        Session.Dispose();
        try { Directory.Delete(_folder, true); } catch { }
    }
}

public class TimelineTests
{
    [Fact]
    public void Fields_are_read_back_in_time()
    {
        using var board = new ReportBoard();
        var id = board.Story("Login", 3);
        board.Advance(1);
        board.Apply(new EditIssue(id, IssueFields.StoryPoints, new FieldValue.Number(5)));
        board.Apply(new TransitionIssue(id, "inprogress"));
        board.Advance(1);
        board.Apply(new TransitionIssue(id, "done"));

        var timeline = new IssueTimeline(board.Snapshot.Issue(id)!, board.Snapshot.HistoryOf(id));

        Assert.Equal(3, timeline.PointsAt(ReportBoard.Start.AddHours(1)));
        Assert.Equal(5, timeline.PointsAt(ReportBoard.Start.AddDays(1).AddHours(1)));
        Assert.Equal("todo", timeline.StatusAt(ReportBoard.Start.AddHours(12)));
        Assert.Equal("inprogress", timeline.StatusAt(ReportBoard.Start.AddDays(1).AddHours(1)));
        Assert.Equal("done", timeline.StatusAt(board.Time.Now));
        Assert.False(timeline.ExistedAt(ReportBoard.Start.AddHours(-1)));
        Assert.Equal(ReportBoard.Start.AddDays(1), timeline.FirstEntered(StatusCategory.InProgress, board.Snapshot.Project));
    }
}

public class SprintReportTests
{
    // A 5-day sprint: two 3-point stories committed, one finished on day 2, one 2-point story added on day 3.
    private static (ReportBoard Board, Sprint Sprint, string A, string B, string C) RunSprint()
    {
        var board = new ReportBoard();
        board.Apply(new CreateSprint { Id = "s1" });
        var a = board.Story("A", 3);
        var b = board.Story("B", 3);
        var c = board.Story("C", 2);
        board.Apply(new PlanIssue(a, "s1", null, null));
        board.Apply(new PlanIssue(b, "s1", null, null));
        board.Apply(new StartSprint("s1", board.Time.Now, board.Time.Now.AddDays(4).AddHours(8)));
        board.Advance(1.2);
        board.Apply(new TransitionIssue(a, "done"));
        board.Advance(1);
        board.Apply(new PlanIssue(c, "s1", null, null));
        board.Advance(2);
        return (board, board.Snapshot.Sprints["s1"], a, b, c);
    }

    [Fact]
    public void Burndown_follows_what_was_done_and_added()
    {
        var (board, sprint, _, _, _) = RunSprint();
        using var _ = board;

        var chart = BoardReports.Sprint(board.Snapshot, sprint, board.Time.Now);

        var remaining = chart.Burndown.Series.Single(s => s.Name == "Remaining").Values;
        Assert.Equal(6, remaining[0]);      // day 1: both stories open
        Assert.Equal(3, remaining[1]);      // day 2: A done
        Assert.Equal(5, remaining[2]);      // day 3: C added
        Assert.Equal(6, chart.Committed);
        var ideal = chart.Burndown.Series.Single(s => s.Name == "Ideal").Values;
        Assert.Equal(6, ideal[0]);
        Assert.Equal(0, ideal[^1]);
        Assert.Equal(8, chart.Scope);
        Assert.Equal(3, chart.Completed);
    }

    [Fact]
    public void The_sprint_report_lists_done_open_added_and_removed()
    {
        var (board, sprint, a, b, c) = RunSprint();
        using var _ = board;
        board.Apply(new PlanIssue(b, null, null, null));   // B taken out
        board.Apply(new CompleteSprint("s1"));

        var report = BoardReports.SprintReportOf(board.Snapshot, board.Snapshot.Sprints["s1"], board.Time.Now);

        Assert.Equal([a], report.Completed.Select(i => i.Id));
        Assert.Equal([c], report.NotCompleted.Select(i => i.Id));
        Assert.Equal([c], report.Added.Select(i => i.Id));
        Assert.Equal([b], report.Removed.Select(i => i.Id));
        Assert.Equal(6, report.CommittedPoints);
        Assert.Equal(3, report.CompletedPoints);
    }

    [Fact]
    public void Velocity_compares_committed_and_completed()
    {
        var (board, _, _, _, _) = RunSprint();
        using var _2 = board;
        board.Apply(new CompleteSprint("s1"));

        var velocity = BoardReports.Velocity(board.Snapshot);

        Assert.Equal(["DEV Sprint 1"], velocity.Labels);
        Assert.Equal(6, velocity.Series[0].Values[0]);
        Assert.Equal(3, velocity.Series[1].Values[0]);
        Assert.Equal(3, BoardReports.AverageVelocity(board.Snapshot));
    }
}

public class FlowTests
{
    [Fact]
    public void Cumulative_flow_counts_issues_per_category_each_day()
    {
        using var board = new ReportBoard();
        var a = board.Story("A", 1);
        board.Story("B", 1);
        board.Advance(1);
        board.Apply(new TransitionIssue(a, "inprogress"));
        board.Advance(1);
        board.Apply(new TransitionIssue(a, "done"));

        var flow = BoardReports.CumulativeFlow(board.Snapshot, board.Time.Now, days: 3);

        var done = flow.Series.Single(s => s.Name == "Done").Values;
        var progress = flow.Series.Single(s => s.Name == "In Progress").Values;
        var todo = flow.Series.Single(s => s.Name == "To Do").Values;
        Assert.Equal([0, 0, 1], done.Select(v => v!.Value));
        Assert.Equal([0, 1, 0], progress.Select(v => v!.Value));
        Assert.Equal([2, 1, 1], todo.Select(v => v!.Value));
    }

    [Fact]
    public void Cycle_time_runs_from_first_in_progress_to_resolved()
    {
        using var board = new ReportBoard();
        var a = board.Story("A", 1);
        board.Advance(1);
        board.Apply(new TransitionIssue(a, "inprogress"));
        board.Advance(2.5);
        board.Apply(new TransitionIssue(a, "done"));

        var cycle = Assert.Single(BoardReports.CycleTimes(board.Snapshot, board.Time.Now));
        Assert.Equal(2.5, cycle.Days, 3);
        var chart = BoardReports.ControlChart([cycle]);
        Assert.Equal(2.5, chart.Series[0].Values[0]);
    }

    [Fact]
    public void Epic_progress_and_counts_by_field()
    {
        using var board = new ReportBoard();
        var epic = BoardIds.New();
        board.Apply(new CreateIssue("epic", "Accounts") { Id = epic });
        var a = board.Story("A", 3);
        var b = board.Story("B", 1);
        board.Apply(new BulkEdit([a, b], [(IssueFields.Parent, new FieldValue.Text(epic))]));
        board.Apply(new TransitionIssue(a, "done"));

        var progress = Assert.Single(BoardReports.Epics(board.Snapshot));
        Assert.Equal((2, 1, 4.0, 3.0), (progress.Issues, progress.DoneIssues, progress.Points, progress.DonePoints));
        Assert.Equal(0.75, progress.Fraction);

        var byStatus = BoardReports.CountBy(board.Snapshot, board.Snapshot.Issues.Values, IssueFields.Status);
        Assert.Equal(["To Do", "Done"], byStatus.Labels);
        Assert.Equal(2, byStatus.Series[0].Values[0]);
    }
}
