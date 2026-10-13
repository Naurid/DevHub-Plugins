using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using Xunit;

namespace DevHub.Board.Core.Tests;

public class SprintTests
{
    private static Sprint Sprint(TestBoard board, string id) => board.Snapshot.Sprints[id];

    [Fact]
    public void Sprints_are_numbered_and_ordered()
    {
        using var board = TestBoard.Create();

        board.Apply(new CreateSprint { Id = "s1" });
        board.Apply(new CreateSprint { Id = "s2", Goal = "Ship sign-in" });

        Assert.Equal("DEV Sprint 1", Sprint(board, "s1").Name);
        Assert.Equal("DEV Sprint 2", Sprint(board, "s2").Name);
        Assert.Equal("Ship sign-in", Sprint(board, "s2").Goal);
        Assert.True(Sprint(board, "s1").Sequence < Sprint(board, "s2").Sequence);

        board.Apply(new MoveSprint("s2", -1));
        Assert.True(Sprint(board, "s2").Sequence < Sprint(board, "s1").Sequence);
    }

    [Fact]
    public void Starting_records_what_was_committed_and_only_one_runs()
    {
        using var board = TestBoard.Create();
        board.Apply(new CreateSprint { Id = "s1" });
        board.Apply(new CreateSprint { Id = "s2" });
        var story = board.Create("story", "Login");
        board.Apply(new EditIssue(story.Id, IssueFields.StoryPoints, new FieldValue.Number(5)));
        var sub = board.Create("subtask", "Layout", parent: story.Id);

        Assert.Throws<BoardRuleException>(() => board.Apply(new StartSprint("s1", TestBoard.Start, TestBoard.Start.AddDays(14))));  // empty
        board.Apply(new PlanIssue(story.Id, "s1", null, null));
        Assert.Equal("s1", board.Get(sub.Id).Sprint);
        Assert.Throws<BoardRuleException>(() => board.Apply(new StartSprint("s1", TestBoard.Start, TestBoard.Start.AddDays(-1))));
        board.Apply(new StartSprint("s1", TestBoard.Start, TestBoard.Start.AddDays(14)) { Goal = "Sign-in" });

        var sprint = Sprint(board, "s1");
        Assert.Equal(SprintState.Active, sprint.State);
        Assert.Equal("Sign-in", sprint.Goal);
        Assert.Equal(5, sprint.CommittedPoints);
        Assert.Single(sprint.Committed);   // sub-tasks count through their parent

        board.Apply(new PlanIssue(board.Create("story", "Other").Id, "s2", null, null));
        Assert.Throws<BoardRuleException>(() => board.Apply(new StartSprint("s2", TestBoard.Start, TestBoard.Start.AddDays(14))));
        Assert.Throws<BoardRuleException>(() => board.Apply(new DeleteSprint("s1")));
    }

    [Theory]
    [InlineData(OpenIssuesGoTo.Backlog)]
    [InlineData(OpenIssuesGoTo.NextSprint)]
    [InlineData(OpenIssuesGoTo.NewSprint)]
    public void Completing_moves_unfinished_issues_and_keeps_finished_ones(OpenIssuesGoTo moveTo)
    {
        using var board = TestBoard.Create();
        board.Apply(new CreateSprint { Id = "s1" });
        board.Apply(new CreateSprint { Id = "s2" });
        var done = board.Create("story", "Done one");
        var open = board.Create("story", "Open one");
        board.Apply(new BulkEdit([done.Id, open.Id], [(IssueFields.StoryPoints, new FieldValue.Number(3))]));
        board.Apply(new PlanIssue(done.Id, "s1", null, null));
        board.Apply(new PlanIssue(open.Id, "s1", null, null));
        board.Apply(new StartSprint("s1", TestBoard.Start, TestBoard.Start.AddDays(14)));
        board.Apply(new TransitionIssue(done.Id, "done"));
        board.Time.Advance(TimeSpan.FromDays(14));

        board.Apply(new CompleteSprint("s1", moveTo) { NextSprint = "s2", NewSprintId = "s3" });

        var sprint = Sprint(board, "s1");
        Assert.Equal(SprintState.Closed, sprint.State);
        Assert.Equal(board.Time.Now, sprint.Completed);
        Assert.Equal(3, sprint.CompletedPoints);
        Assert.Equal(open.Id, Assert.Single(sprint.NotCompleted).Issue);
        Assert.Equal("s1", board.Get(done.Id).Sprint);
        var expected = moveTo switch { OpenIssuesGoTo.NextSprint => "s2", OpenIssuesGoTo.NewSprint => "s3", _ => null };
        Assert.Equal(expected, board.Get(open.Id).Sprint);
        if (moveTo == OpenIssuesGoTo.NewSprint)
            Assert.Equal(SprintState.Future, Sprint(board, "s3").State);
        Assert.Throws<BoardRuleException>(() => board.Apply(new DeleteSprint("s1")));
    }

    [Fact]
    public void Deleting_a_future_sprint_returns_its_issues_to_the_backlog()
    {
        using var board = TestBoard.Create();
        board.Apply(new CreateSprint { Id = "s1" });
        var story = board.Create("story", "Login");
        board.Apply(new PlanIssue(story.Id, "s1", null, null));

        board.Apply(new DeleteSprint("s1"));

        Assert.Null(board.Get(story.Id).Sprint);
        Assert.Empty(board.Snapshot.Sprints);
    }

    [Fact]
    public void Planning_moves_and_ranks_in_one_change()
    {
        using var board = TestBoard.Create();
        board.Apply(new CreateSprint { Id = "s1" });
        var a = board.Create("story", "A");
        var b = board.Create("story", "B");
        board.Apply(new PlanIssue(a.Id, "s1", null, null));

        var change = board.Apply(new PlanIssue(b.Id, "s1", null, a.Id));

        Assert.Equal("s1", board.Get(b.Id).Sprint);
        Assert.True(string.CompareOrdinal(board.Get(b.Id).Rank, board.Get(a.Id).Rank) < 0);
        Assert.Contains("ranked", change.Message);
    }

    [Fact]
    public void Edit_checks_names_and_dates()
    {
        using var board = TestBoard.Create();
        board.Apply(new CreateSprint { Id = "s1" });
        board.Apply(new CreateSprint { Id = "s2" });

        Assert.Throws<BoardRuleException>(() => board.Apply(new EditSprint("s1", "DEV Sprint 2", "", null, null, null)));
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditSprint("s1", "X", "", TestBoard.Start, TestBoard.Start, null)));
        board.Apply(new EditSprint("s1", "Kick-off", "Goal", TestBoard.Start, TestBoard.Start.AddDays(7), 20));

        Assert.Equal((("Kick-off", 20.0)), (Sprint(board, "s1").Name, Sprint(board, "s1").Capacity!.Value));
    }
}
