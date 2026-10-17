using DevHub.Board.Core.Fields;
using Xunit;

namespace DevHub.Board.Core.Tests;

public class EpicTests
{
    [Fact]
    public void Open_task_lines_are_found_in_their_forms_and_checked_ones_are_left()
    {
        const string description = "Goal: ship it.\n\n- [ ] Write the docs\r\n* [ ]  Fix the build  \n-[] Tag it\n- [x] Already done\n- a list item\n- [ ]   ";

        Assert.Equal(["Write the docs", "Fix the build", "Tag it"], TaskList.Items(description));
        Assert.Equal("Goal: ship it.\n\n- [x] Already done\n- a list item\n- [ ]", TaskList.Without(description));
        Assert.Empty(TaskList.Items(null));
    }

    [Fact]
    public async Task An_epic_is_created_with_its_tasks_in_one_change()
    {
        using var board = TestBoard.Create();
        var start = new DateOnly(2026, 11, 2);
        var due = new DateOnly(2026, 11, 20);
        IReadOnlyList<(string, FieldValue)> shared =
        [
            (IssueFields.Labels, new FieldValue.Items(["ui"])),
            (IssueFields.Start, new FieldValue.Date(start)),
            (IssueFields.Due, new FieldValue.Date(due)),
        ];

        board.Apply(new CreateIssueWithChildren(
            new CreateIssue("epic", "Search") { Id = "epic0001", Description = "Find things.", Fields = shared },
            [new CreateIssue("task", "Parser") { Fields = shared }, new CreateIssue("task", "Results page") { Fields = shared }]));

        var epic = board.Get("epic0001");
        Assert.Equal("DEV-1", epic.Key);
        var tasks = board.Snapshot.ChildrenOf(epic.Id).OrderBy(t => t.Rank, StringComparer.Ordinal).ToList();
        Assert.Equal(["DEV-2 Parser", "DEV-3 Results page"], tasks.Select(t => $"{t.Key} {t.Summary}"));
        Assert.All(tasks, t =>
        {
            Assert.Equal("task", t.Type);
            Assert.Equal(["ui"], t.Labels);
            Assert.Equal(start, t.Start);
            Assert.Equal(due, t.Due);
            Assert.Equal("created", Assert.Single(board.Snapshot.HistoryOf(t.Id)).Field);
        });
        Assert.Equal(4, board.Snapshot.Project.NextNumber);
        Assert.Equal("DEV-1: created \"Search\" with 2 issue(s)", board.Session.UndoDescription);

        // Reloaded from disk it's the same, and one Undo takes all three back.
        Assert.Equal(3, board.Reload().Issues.Count);
        await board.Session.UndoAsync();
        Assert.Empty(board.Snapshot.Issues);
    }

    [Fact]
    public void A_bad_child_refuses_the_whole_change()
    {
        using var board = TestBoard.Create();

        Assert.Throws<BoardRuleException>(() => board.Apply(new CreateIssueWithChildren(
            new CreateIssue("epic", "Search"), [new CreateIssue("task", "Fine"), new CreateIssue("task", " ")])));
        Assert.Empty(board.Snapshot.Issues);
    }

    [Fact]
    public void A_new_issue_in_an_epic_starts_with_its_dates_and_labels()
    {
        using var board = TestBoard.Create();
        var epic = board.Create("epic", "Search");
        var story = board.Create("story", "Parser", epic.Id);
        board.Apply(new EditIssue(epic.Id, IssueFields.Start, new FieldValue.Date(new DateOnly(2026, 11, 2))));
        board.Apply(new EditIssue(epic.Id, IssueFields.Labels, new FieldValue.Items(["ui", "search"])));

        var defaults = EpicDefaults.For(board.Snapshot, epic.Id);
        Assert.Equal([IssueFields.Start, IssueFields.Labels], defaults.Select(d => d.Field));   // no due date to copy
        Assert.Empty(EpicDefaults.For(board.Snapshot, story.Id));   // a sub-task's parent isn't an epic
        Assert.Empty(EpicDefaults.For(board.Snapshot, null));

        // Fields set on the issue itself win.
        var fields = EpicDefaults.Add(board.Snapshot, epic.Id, [(IssueFields.Labels, new FieldValue.Items(["backend"]))]);
        Assert.Equal([new FieldValue.Items(["backend"]), new FieldValue.Date(new DateOnly(2026, 11, 2))], fields.Select(f => f.Value));
    }
}
