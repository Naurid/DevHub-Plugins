using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using Xunit;

namespace DevHub.Board.Core.Tests;

public class BoardColumnTests
{
    private static BoardDefinition Def(TestBoard board) => board.Snapshot.Boards.Values.Single();

    [Fact]
    public void A_new_column_creates_its_status()
    {
        using var board = TestBoard.Create();

        board.Apply(new AddColumn(Def(board).Id, "QA", StatusCategory.InProgress) { Index = 3, ColumnId = "qa", StatusId = "qa" });

        Assert.Equal(["To Do", "In Progress", "In Review", "QA", "Done"], Def(board).Columns.Select(c => c.Name));
        Assert.Equal(["qa"], Def(board).Columns[3].Statuses);
        Assert.Equal(StatusCategory.InProgress, board.Snapshot.Project.Status("qa")!.Category);
        Assert.Contains("qa", board.Snapshot.DefaultWorkflow!.Statuses);
    }

    [Fact]
    public void A_new_column_takes_an_existing_status_that_is_not_on_the_board()
    {
        using var board = TestBoard.Create();
        var review = Def(board).Columns.Single(c => c.Name == "In Review");
        board.Apply(new DeleteColumn(Def(board).Id, review.Id));
        var statuses = board.Snapshot.Project.Statuses.Count;

        board.Apply(new AddColumn(Def(board).Id, "in review"));

        Assert.Equal(statuses, board.Snapshot.Project.Statuses.Count);
        Assert.Equal(["inreview"], Def(board).Columns[^1].Statuses);
        Assert.Throws<BoardRuleException>(() => board.Apply(new AddColumn(Def(board).Id, "Done ")));   // a column of that name exists
    }

    [Fact]
    public void Renaming_a_column_renames_its_own_status()
    {
        using var board = TestBoard.Create();
        var review = Def(board).Columns.Single(c => c.Name == "In Review");

        board.Apply(new RenameColumn(Def(board).Id, review.Id, "Code Review"));

        Assert.Equal("Code Review", board.Snapshot.Project.Status("inreview")!.Name);
        Assert.Equal("Code Review", Def(board).Columns.Single(c => c.Id == review.Id).Name);
    }

    [Fact]
    public void Columns_can_be_moved_and_merged()
    {
        using var board = TestBoard.Create();
        var columns = Def(board).Columns;

        board.Apply(new MoveColumn(Def(board).Id, columns[3].Id, 0));
        Assert.Equal("Done", Def(board).Columns[0].Name);

        board.Apply(new MergeColumns(Def(board).Id, columns[2].Id, columns[1].Id));
        var merged = Def(board).Columns.Single(c => c.Id == columns[1].Id);
        Assert.Equal(["inprogress", "inreview"], merged.Statuses);
        Assert.Equal(3, Def(board).Columns.Count);
    }

    [Fact]
    public void Every_column_can_go_and_issues_keep_their_status()
    {
        using var board = TestBoard.Create(BoardPreset.Kanban);
        var issue = board.Create("story", "Login");

        foreach (var column in Def(board).Columns.ToList())
            board.Apply(new DeleteColumn(Def(board).Id, column.Id));

        Assert.Empty(Def(board).Columns);
        Assert.Equal("backlog", board.Get(issue.Id).Status);
        var layout = BoardLayout.Build(board.Snapshot, Def(board), new BoardViewOptions(), board.Context);
        Assert.Equal(("Backlog", 1), (layout.NotOnBoard.Single().Status.Name, layout.NotOnBoard.Single().Count));
    }

    [Fact]
    public void Deleting_a_column_with_its_statuses_moves_the_issues()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");
        board.Apply(new TransitionIssue(issue.Id, "inreview"));
        var review = Def(board).Columns.Single(c => c.Name == "In Review");

        Assert.Throws<BoardRuleException>(() => board.Apply(new DeleteColumn(Def(board).Id, review.Id) { DeleteStatuses = true }));
        board.Apply(new DeleteColumn(Def(board).Id, review.Id) { DeleteStatuses = true, MoveIssuesTo = "inprogress" });

        Assert.Equal("inprogress", board.Get(issue.Id).Status);
        Assert.Null(board.Snapshot.Project.Status("inreview"));
        Assert.DoesNotContain(Def(board).Columns, c => c.Id == review.Id);
    }

    [Fact]
    public void A_status_is_on_at_most_one_column_of_a_board()
    {
        using var board = TestBoard.Create();
        var columns = Def(board).Columns;

        board.Apply(new SetColumnStatuses(Def(board).Id, columns[1].Id, ["inprogress", "inreview"]));

        Assert.Empty(Def(board).Columns.Single(c => c.Id == columns[2].Id).Statuses);
        Assert.Equal(columns[1].Id, Def(board).ColumnOf("inreview")!.Id);
    }

    [Fact]
    public void Wip_limits_are_checked_and_shown()
    {
        using var board = TestBoard.Create(BoardPreset.Kanban);
        var todo = Def(board).Columns[0];
        board.Create("story", "A");
        board.Create("story", "B");

        Assert.Throws<BoardRuleException>(() => board.Apply(new SetWipLimits(Def(board).Id, todo.Id, 3, 2)));
        board.Apply(new SetWipLimits(Def(board).Id, todo.Id, null, 1));
        board.Apply(new SetWipLimits(Def(board).Id, Def(board).Columns[1].Id, 1, null));

        var layout = BoardLayout.Build(board.Snapshot, Def(board), new BoardViewOptions(), board.Context);
        Assert.True(layout.Columns[0].OverLimit);
        Assert.True(layout.Columns[1].UnderMinimum);
    }

    [Fact]
    public void Another_board_can_have_its_own_columns_and_the_last_board_stays()
    {
        using var board = TestBoard.Create();

        board.Apply(new CreateBoardView("Bugs", BoardKind.Kanban) { Id = "bugs", Filter = "type = bug" });
        board.Apply(new AddColumn("bugs", "Triage", StatusCategory.ToDo));

        Assert.Equal(2, board.Snapshot.Boards.Count);
        Assert.Equal("Triage", board.Snapshot.Boards["bugs"].Columns[^1].Name);
        Assert.DoesNotContain(board.Snapshot.Boards.Values.Single(b => b.Id != "bugs").Columns, c => c.Name == "Triage");
        Assert.Throws<BoardRuleException>(() => board.Apply(new CreateBoardView("Broken", BoardKind.Kanban) { Filter = "colour = red" }));

        board.Apply(new DeleteBoardView("bugs"));
        Assert.Throws<BoardRuleException>(() => board.Apply(new DeleteBoardView(Def(board).Id)));
    }

    [Fact]
    public void Quick_filters_can_be_added_changed_and_removed()
    {
        using var board = TestBoard.Create();
        var id = Def(board).Id;

        board.Apply(new SetQuickFilter(id, null, "Bugs", "type = bug"));
        var bugs = Def(board).QuickFilters.Single(q => q.Name == "Bugs");
        board.Apply(new SetQuickFilter(id, bugs.Id, "Open bugs", "type = bug and statusCategory != done"));
        Assert.Equal("Open bugs", Def(board).QuickFilters.Single(q => q.Id == bugs.Id).Name);
        Assert.Throws<BoardRuleException>(() => board.Apply(new SetQuickFilter(id, null, "Bad", "status = Nope")));

        board.Apply(new SetQuickFilter(id, bugs.Id, "", null));
        Assert.DoesNotContain(Def(board).QuickFilters, q => q.Id == bugs.Id);
    }
}

public class MoveCardTests
{
    private static BoardDefinition Def(TestBoard board) => board.Snapshot.Boards.Values.Single();

    [Fact]
    public void Dropping_a_card_in_a_column_transitions_and_ranks_it()
    {
        using var board = TestBoard.Create(BoardPreset.Kanban);
        var a = board.Create("story", "A");
        var b = board.Create("story", "B");
        var c = board.Create("story", "C");
        board.Apply(new TransitionIssue(a.Id, "inprogress"));
        board.Apply(new TransitionIssue(b.Id, "inprogress"));
        var inProgress = Def(board).Columns.Single(col => col.Name == "In Progress");

        var change = board.Apply(new MoveCard(Def(board).Id, c.Id, inProgress.Id, a.Id, b.Id));

        Assert.Equal("inprogress", board.Get(c.Id).Status);
        var layout = BoardLayout.Build(board.Snapshot, Def(board), new BoardViewOptions(), board.Context);
        Assert.Equal([a.Id, c.Id, b.Id], layout.Columns.Single(col => col.Column.Id == inProgress.Id).Cards.Select(i => i.Id));
        Assert.Contains("status", change.Message);
        Assert.Contains(board.Snapshot.HistoryOf(c.Id), h => h.Field == IssueFields.Status);
    }

    [Fact]
    public void A_column_whose_status_the_workflow_forbids_refuses_the_card()
    {
        using var board = TestBoard.Create();
        var workflow = board.Snapshot.DefaultWorkflow!;
        board.Apply(new SetTransitions(workflow.Id, false, [new WorkflowTransition { From = "todo", To = "inprogress" }]));
        var issue = board.Create("story", "Login");
        var done = Def(board).Columns.Single(c => c.Name == "Done");

        Assert.Throws<BoardRuleException>(() => board.Apply(new MoveCard(Def(board).Id, issue.Id, done.Id, null, null)));
        Assert.Null(MoveCard.TargetStatus(board.Snapshot, issue, done));
    }

    [Fact]
    public void A_column_with_several_statuses_takes_the_first_allowed_one()
    {
        using var board = TestBoard.Create();
        var columns = Def(board).Columns;
        board.Apply(new MergeColumns(Def(board).Id, columns[2].Id, columns[1].Id));   // In Progress + In Review
        var issue = board.Create("story", "Login");

        board.Apply(new MoveCard(Def(board).Id, issue.Id, columns[1].Id, null, null));

        Assert.Equal("inprogress", board.Get(issue.Id).Status);
    }
}

public class BoardLayoutTests
{
    private static BoardDefinition Def(TestBoard board) => board.Snapshot.Boards.Values.Single();

    [Fact]
    public void A_scrum_board_shows_only_the_active_sprint()
    {
        using var board = TestBoard.Create();
        board.AddSprint("s1", "Sprint 1", SprintState.Active);
        var planned = board.Create("story", "Planned");
        board.Create("story", "Backlog");
        board.Apply(new EditIssue(planned.Id, IssueFields.Sprint, new FieldValue.Text("s1")));

        var layout = BoardLayout.Build(board.Snapshot, Def(board), new BoardViewOptions(), board.Context);

        Assert.Equal([planned.Id], layout.Columns.SelectMany(c => c.Cards).Select(i => i.Id));
        Assert.Equal("s1", layout.ActiveSprints.Single().Id);
    }

    [Fact]
    public void Kanban_boards_hide_done_issues_after_a_while_and_epics_always()
    {
        using var board = TestBoard.Create(BoardPreset.Kanban);
        var done = board.Create("story", "Old");
        board.Apply(new TransitionIssue(done.Id, "done"));
        board.Create("epic", "An epic");
        var open = board.Create("story", "Open");

        Assert.Equal(2, BoardLayout.Build(board.Snapshot, Def(board), new BoardViewOptions(), board.Context).CardCount);
        board.Time.Advance(TimeSpan.FromDays(15));
        var later = BoardLayout.Build(board.Snapshot, Def(board), new BoardViewOptions(), board.Context);
        Assert.Equal([open.Id], later.Columns.SelectMany(c => c.Cards).Select(i => i.Id));
    }

    [Fact]
    public void Quick_filters_search_and_assignees_narrow_the_cards()
    {
        using var board = TestBoard.Create(BoardPreset.Kanban);
        board.Apply(new AddPerson("Grace Hopper") { Id = "grace" });
        var mine = board.Create("story", "Login form");
        var hers = board.Create("bug", "Crash");
        board.Apply(new EditIssue(mine.Id, IssueFields.Assignee, new FieldValue.Text(board.Session.CurrentPersonId!)));
        board.Apply(new EditIssue(hers.Id, IssueFields.Assignee, new FieldValue.Text("grace")));
        var onlyMine = Def(board).QuickFilters.Single(q => q.Name == "Only my issues").Id;

        string[] Cards(BoardViewOptions o) => BoardLayout.Build(board.Snapshot, Def(board), o, board.Context).Columns
            .SelectMany(c => c.Cards).Select(i => i.Key).ToArray();

        Assert.Equal(["DEV-1"], Cards(new BoardViewOptions { QuickFilters = new HashSet<string> { onlyMine } }));
        Assert.Equal(["DEV-2"], Cards(new BoardViewOptions { Search = "crash" }));
        Assert.Equal(["DEV-2"], Cards(new BoardViewOptions { Search = "dev-2" }));
        Assert.Equal(["DEV-2"], Cards(new BoardViewOptions { Assignees = new HashSet<string> { "grace" } }));
    }

    [Fact]
    public void Swimlanes_group_cards_with_the_none_lane_last()
    {
        using var board = TestBoard.Create(BoardPreset.Kanban);
        board.Apply(new EditBoardView(Def(board).Id) { Swimlanes = SwimlaneMode.Epic });
        var epic = board.Create("epic", "Accounts");
        board.Create("story", "Loose");
        board.Create("story", "Login", parent: epic.Id);

        var layout = BoardLayout.Build(board.Snapshot, Def(board), new BoardViewOptions(), board.Context);

        Assert.Equal(["DEV-1 Accounts", "No epic"], layout.Swimlanes.Select(l => l.Title));
        Assert.Equal(1, layout.Swimlanes[0].Count);
        Assert.Equal(layout.Columns.Count, layout.Swimlanes[0].Cells.Count);
    }

    [Fact]
    public void A_broken_board_filter_is_reported_not_fatal()
    {
        using var board = TestBoard.Create();
        board.WriteRaw(Model.BoardPathsFor(Def(board)), Def(board) with { Filter = "colour = red" });

        var layout = BoardLayout.Build(board.Snapshot, Def(board), new BoardViewOptions(), board.Context);

        Assert.NotNull(layout.Problem);
    }
}

internal static class Model
{
    public static string BoardPathsFor(BoardDefinition definition) => BoardPaths.Board(definition.Id);
}
