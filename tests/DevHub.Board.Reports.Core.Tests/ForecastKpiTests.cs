using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using Xunit;

namespace DevHub.Board.Reports.Core.Tests;

public class ForecastTests
{
    // Runs a two-week sprint finishing the given stories (each done before the sprint completes).
    private static void RunSprint(ReportBoard board, string id, params string[] done)
    {
        board.Apply(new CreateSprint { Id = id });
        foreach (var issue in done)
            board.Apply(new PlanIssue(issue, id, null, null));
        board.Apply(new StartSprint(id, board.Time.Now, board.Time.Now.AddDays(13)));
        board.Advance(5);
        foreach (var issue in done)
            board.Apply(new TransitionIssue(issue, "done"));
        board.Advance(9);
        board.Apply(new CompleteSprint(id));
    }

    [Fact]
    public void Velocities_come_from_the_last_sprints()
    {
        using var board = new ReportBoard();
        Assert.Null(ScenarioVelocities.FromHistory(board.Snapshot));
        RunSprint(board, "s1", board.Story("A", 5));
        RunSprint(board, "s2", board.Story("B", 8), board.Story("C", 3));
        RunSprint(board, "s3", board.Story("D", 8));

        Assert.Equal(new ScenarioVelocities(5, 8, 11), ScenarioVelocities.FromHistory(board.Snapshot));
    }

    [Fact]
    public void Each_scenario_gets_its_sprints_and_date()
    {
        using var board = new ReportBoard();
        var scope = Enumerable.Range(1, 6).Select(i => board.Story($"S{i}", 5)).ToList();   // 30 points
        board.Apply(new TransitionIssue(scope[0], "done"));                                     // 25 left
        var today = DateOnly.FromDateTime(board.Time.Now.LocalDateTime);

        var forecast = Forecasts.Build(board.Snapshot, scope.Select(id => board.Snapshot.Issue(id)!).ToList(),
            new ScenarioVelocities(5, 10, 15), today, target: today.AddDays(27));

        Assert.Equal((30, 5, 25), (forecast.ScopePoints, forecast.DonePoints, forecast.Remaining));
        Assert.Equal([5, 3, 2], forecast.Scenarios.Select(s => s.Sprints!.Value));
        Assert.Equal(today.AddDays(3 * 14 - 1), forecast.Scenarios[1].Delivery);
        Assert.Equal(14, forecast.Scenarios[1].DaysLate);   // 41 days vs a 27-day target
        Assert.Equal(0, forecast.Scenarios[2].DaysLate);    // two sprints: exactly the target
        // The chart: done, scope, three projections, and the range between pessimistic and optimistic.
        Assert.Equal(["Done", "Scope", "Pessimistic", "Neutral", "Optimistic"], forecast.Chart.Series.Select(s => s.Name));
        Assert.Single(forecast.Chart.Bands);
    }

    [Fact]
    public void A_forming_team_plans_a_slower_start()
    {
        using var board = new ReportBoard();
        var scope = Enumerable.Range(1, 4).Select(i => board.Story($"S{i}", 10)).Select(id => board.Snapshot.Issue(id)!).ToList();   // 40 points
        var today = DateOnly.FromDateTime(board.Time.Now.LocalDateTime);

        var steady = Forecasts.Build(board.Snapshot, scope, new ScenarioVelocities(10, 20, 30), today);
        var forming = Forecasts.Build(board.Snapshot, scope, new ScenarioVelocities(10, 20, 30), today, planned: [5, 10, 20]);

        Assert.Equal(2, steady.Scenarios[1].Sprints);
        Assert.Equal(4, forming.Scenarios[1].Sprints);   // 5, 15, 35, then 55 ≥ 40 (the last planned velocity goes on)
    }
}

public class KpiTests
{
    [Fact]
    public void Scope_changes_list_what_was_added_re_estimated_and_dropped()
    {
        using var board = new ReportBoard();
        var a = board.Story("A", 3);
        var b = board.Story("B", 5);
        board.Advance(2);
        var from = board.Time.Now;
        board.Advance(1);
        var c = board.Story("C", 8);
        board.Advance(0.1);
        board.Apply(new EditIssue(a, IssueFields.StoryPoints, new FieldValue.Number(5)));
        board.Advance(0.1);
        board.Apply(new TransitionIssue(b, "done") { Resolution = "wontdo" });
        board.Advance(1);

        var scope = new[] { a, b, c }.Select(id => board.Snapshot.Issue(id)!).ToList();
        var changes = Kpis.ScopeChanges(board.Snapshot, scope, from, board.Time.Now);

        Assert.Equal(["added", "re-estimated 3 → 5", "dropped (Won't Do)"], changes.Select(x => x.What));
        Assert.Equal([8.0, 2, -5], changes.Select(x => x.Points));
        var chart = Kpis.ScopeChangeChart(board.Snapshot, changes);
        Assert.Equal([10.0], chart.Series[0].Values.Select(v => v!.Value));
        Assert.Equal([5.0], chart.Series[1].Values.Select(v => v!.Value));
    }

    [Fact]
    public void Sprint_plans_list_every_sprint_with_its_stories()
    {
        using var board = new ReportBoard();
        var a = board.Story("Login", 3);
        board.Apply(new CreateSprint { Id = "s1", Name = "Sprint 1" });
        board.Apply(new PlanIssue(a, "s1", null, null));
        board.Apply(new EditSprint("s1", "Sprint 1", "Sign in", null, null, 10));

        var table = Kpis.SprintPlans(board.Snapshot);

        var row = Assert.Single(table.Rows);
        Assert.Equal("Sprint 1", row[0]);
        Assert.Equal("Sign in", row[4]);
        Assert.Equal("10", row[5]);
        Assert.Equal("3", row[8]);   // planned
        Assert.Equal("DEV-1 Login (3)", row[10]);
        Assert.NotEmpty(Kpis.Explain("reliability"));
    }
}
