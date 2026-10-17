using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using Xunit;

namespace DevHub.Board.Core.Tests;

public class CapacityTests
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Working_days_skip_weekends()
    {
        Assert.Equal(10, SprintCapacity.WorkingDays(Monday, Monday.AddDays(13)));   // two weeks
        Assert.Equal(1, SprintCapacity.WorkingDays(Monday, Monday));
        Assert.Equal(0, SprintCapacity.WorkingDays(Monday.AddDays(5), Monday.AddDays(6)));   // a weekend
        Assert.Equal(0, SprintCapacity.WorkingDays(null, Monday));
    }

    [Fact]
    public void Availability_and_focus_are_saved_with_the_sprint()
    {
        using var board = TestBoard.Create();
        var me = board.Session.CurrentPersonId!;
        board.AddSprint("s1", "Sprint 1", SprintState.Future);

        board.Apply(new EditSprint("s1", "Sprint 1", "", Monday, Monday.AddDays(13), 12)
        {
            Availability = [new MemberDays { Person = me, Days = 8 }],
            FocusFactor = 0.75,
        });

        var sprint = board.Snapshot.Sprints["s1"];
        Assert.Equal(8, sprint.PersonDays);
        Assert.Equal(6, sprint.FocusedDays);
        Assert.Equal(12, sprint.Capacity);

        // Without availability, an edit keeps what's there.
        board.Apply(new EditSprint("s1", "Sprint 1", "Ship it", Monday, Monday.AddDays(13), 14));
        Assert.Equal(6, board.Snapshot.Sprints["s1"].FocusedDays);

        Assert.Throws<BoardRuleException>(() => board.Apply(new EditSprint("s1", "Sprint 1", "", null, null, null)
        {
            Availability = [new MemberDays { Person = "nobody", Days = 3 }],
        }));
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditSprint("s1", "Sprint 1", "", null, null, null)
        {
            Availability = [new MemberDays { Person = me, Days = 3 }],
            FocusFactor = 1.5,
        }));
    }

    [Fact]
    public void The_suggestion_comes_from_points_per_focused_day_of_the_last_sprints()
    {
        using var board = TestBoard.Create();
        var me = board.Session.CurrentPersonId!;
        Sprint Closed(string id, int week, double focusedDays, double done) => new()
        {
            Id = id, Name = id, State = SprintState.Closed, Start = Monday.AddDays(14 * week), Completed = Monday.AddDays(14 * week + 13),
            Availability = [new MemberDays { Person = me, Days = focusedDays }],
            CompletedIssues = [new SprintIssuePoints { Issue = "x", Points = done }],
        };
        Assert.Null(SprintCapacity.Suggested(board.Snapshot, 10));   // a new team: no history

        board.WriteRaw(BoardPaths.Sprint("a"), Closed("a", 0, 10, 100));   // oldest: left out (only the last 3 count)
        board.WriteRaw(BoardPaths.Sprint("b"), Closed("b", 1, 10, 5));
        board.WriteRaw(BoardPaths.Sprint("c"), Closed("c", 2, 10, 10));
        board.WriteRaw(BoardPaths.Sprint("d"), Closed("d", 3, 20, 15));

        Assert.Equal(0.75, SprintCapacity.PointsPerFocusedDay(board.Snapshot));   // 30 points / 40 days
        Assert.Equal(7.5, SprintCapacity.Suggested(board.Snapshot, 10));
    }
}

public class RetroTests
{
    [Fact]
    public void A_retrospective_is_saved_cleaned_and_read_back()
    {
        using var board = TestBoard.Create();
        var me = board.Session.CurrentPersonId!;
        board.AddSprint("s1", "Sprint 1", SprintState.Closed);
        Assert.Empty(Retrospectives.Of(board.Snapshot, "s1").WentWell);

        board.Apply(new SaveRetrospective(new Retrospective
        {
            Sprint = "s1",
            WentWell = [new RetroNote { Text = " Pairing helped " }, new RetroNote { Text = "  " }],
            Problems = [new RetroNote { Text = "Stories too big" }],
            Actions = [new RetroAction { Text = "Split stories over 5 points", Owner = me }],
            FollowUp = "Checked at every planning.",
        }));

        var retro = Retrospectives.Of(board.Reload(), "s1");
        Assert.Equal(["Pairing helped"], retro.WentWell.Select(n => n.Text));
        Assert.False(string.IsNullOrEmpty(retro.WentWell[0].Id));
        Assert.Equal(me, Assert.Single(retro.Actions).Owner);
        Assert.Equal(TestBoard.Start, retro.Updated);

        Assert.Throws<BoardRuleException>(() => board.Apply(new SaveRetrospective(new Retrospective { Sprint = "gone" })));
        Assert.Throws<BoardRuleException>(() => board.Apply(new SaveRetrospective(new Retrospective
        {
            Sprint = "s1", Actions = [new RetroAction { Text = "x", Owner = "nobody" }],
        })));
    }

    [Fact]
    public void Open_actions_of_earlier_sprints_come_back_until_done()
    {
        using var board = TestBoard.Create();
        board.WriteRaw(BoardPaths.Sprint("s1"), new Sprint { Id = "s1", Name = "Sprint 1", State = SprintState.Closed, Start = TestBoard.Start });
        board.WriteRaw(BoardPaths.Sprint("s2"), new Sprint { Id = "s2", Name = "Sprint 2", State = SprintState.Active, Start = TestBoard.Start.AddDays(14) });
        board.Apply(new SaveRetrospective(new Retrospective
        {
            Sprint = "s1",
            Actions = [new RetroAction { Id = "a1", Text = "Split big stories" }, new RetroAction { Id = "a2", Text = "Done already", Done = true }],
        }));

        var open = Retrospectives.OpenActionsBefore(board.Snapshot, "s2");
        Assert.Equal("Split big stories", Assert.Single(open).Action.Text);
        Assert.Empty(Retrospectives.OpenActionsBefore(board.Snapshot, "s1"));

        board.Apply(new SetRetroActionDone("s1", "a1", true));
        Assert.Empty(Retrospectives.OpenActionsBefore(board.Snapshot, "s2"));
    }

    [Fact]
    public void A_retrospective_reads_as_markdown()
    {
        using var board = TestBoard.Create();
        board.AddSprint("s1", "Sprint 1", SprintState.Closed);
        var story = board.Create("story", "Split checkout");
        board.Apply(new SaveRetrospective(new Retrospective
        {
            Sprint = "s1",
            WentWell = [new RetroNote { Text = "Demo went well" }],
            Actions = [new RetroAction { Text = "Smaller stories", Issue = story.Id }],
        }));

        var markdown = Retrospectives.ToMarkdown(board.Snapshot, Retrospectives.Of(board.Snapshot, "s1"));

        Assert.Contains("# Retrospective: Sprint 1", markdown);
        Assert.Contains("- Demo went well", markdown);
        Assert.Contains($"- [ ] Smaller stories → {story.Key}", markdown);
        Assert.Contains("_Nothing noted._", markdown);   // problems
    }
}
