using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Import;
using DevHub.Board.Core.Model;
using Xunit;

namespace DevHub.Board.Core.Tests;

public class DoneTests
{
    [Fact]
    public void A_scrum_board_starts_with_a_definition_of_done_and_business_value()
    {
        using var board = TestBoard.Create(BoardPreset.Scrum);
        var project = board.Snapshot.Project;

        Assert.Equal(6, project.DefinitionOfDone.Count);
        Assert.Equal(DoneCheck.Warn, project.Settings.DoneCheck);
        Assert.Equal("Business value", Assert.Single(project.CustomFields).Name);

        using var kanban = TestBoard.Create(BoardPreset.Kanban);
        Assert.Empty(kanban.Snapshot.Project.DefinitionOfDone);
    }

    [Fact]
    public void Criteria_are_added_ticked_edited_moved_and_removed_with_history()
    {
        using var board = TestBoard.Create();
        var story = board.Create("story", "Checkout");

        board.Apply(new AddCriterion(story.Id, "  Totals include VAT ") { Id = "c1" });
        board.Apply(new AddCriterion(story.Id, "Card payments work") { Id = "c2" });
        board.Apply(new CheckCriterion(story.Id, "c1", true));
        board.Apply(new EditCriterion(story.Id, "c2", "Card and PayPal payments work"));
        board.Apply(new MoveCriterion(story.Id, "c2", 0));

        var criteria = board.Get(story.Id).Criteria;
        Assert.Equal(["Card and PayPal payments work", "Totals include VAT"], criteria.Select(c => c.Text));
        Assert.Equal([false, true], criteria.Select(c => c.Done));

        board.Apply(new RemoveCriterion(story.Id, "c1"));
        Assert.Single(board.Get(story.Id).Criteria);
        var fields = board.Snapshot.HistoryOf(story.Id).Select(h => h.Field).ToList();
        Assert.Equal(["created", "criteria", "criteria", "criteria-met", "criteria", "criteria"], fields);

        Assert.Throws<BoardRuleException>(() => board.Apply(new AddCriterion(story.Id, "   ")));
        Assert.Throws<BoardRuleException>(() => board.Apply(new CheckCriterion(story.Id, "nope", true)));
    }

    [Fact]
    public void Create_issue_takes_criteria_lines()
    {
        using var board = TestBoard.Create();

        board.Apply(new CreateIssue("story", "Search") { Id = "s1", Criteria = ["Finds by name", "", "  Shows 20 per page "] });

        Assert.Equal(["Finds by name", "Shows 20 per page"], board.Get("s1").Criteria.Select(c => c.Text));
        Assert.All(board.Get("s1").Criteria, c => Assert.False(c.Done));
    }

    [Fact]
    public void The_definition_of_done_is_edited_and_ticked_per_issue()
    {
        using var board = TestBoard.Create(BoardPreset.Kanban);
        board.Apply(new AddDoneItem("Code reviewed") { Id = "review" });
        board.Apply(new AddDoneItem("Tests pass") { Id = "tests" });
        board.Apply(new EditDoneItem("tests", "Unit tests pass"));
        board.Apply(new MoveDoneItem("tests", 0));
        Assert.Throws<BoardRuleException>(() => board.Apply(new AddDoneItem("code reviewed")));
        Assert.Equal(["Unit tests pass", "Code reviewed"], board.Snapshot.Project.DefinitionOfDone.Select(d => d.Text));

        var story = board.Create("story", "Login");
        board.Apply(new CheckDoneItem(story.Id, "review", true));
        Assert.Equal(["review"], board.Get(story.Id).DoneChecks);
        Assert.Equal(["Definition of Done: Unit tests pass"], DoneReadiness.Missing(board.Snapshot.Project, board.Get(story.Id)));

        board.Apply(new CheckDoneItem(story.Id, "review", false));
        Assert.Empty(board.Get(story.Id).DoneChecks);

        board.Apply(new RemoveDoneItem("review"));
        Assert.Equal(["tests"], board.Snapshot.Project.DefinitionOfDone.Select(d => d.Id));
    }

    [Fact]
    public void Epics_and_subtasks_only_need_their_criteria()
    {
        using var board = TestBoard.Create();
        var epic = board.Create("epic", "Payments");
        var story = board.Create("story", "Cards", epic.Id);
        var subtask = board.Create("subtask", "API", story.Id);
        var project = board.Snapshot.Project;

        Assert.Empty(DoneReadiness.Missing(project, epic));
        Assert.Empty(DoneReadiness.Missing(project, subtask));
        Assert.Equal(6, DoneReadiness.Missing(project, story).Count);
    }

    [Fact]
    public void Warn_lists_what_a_change_would_move_to_done_unfinished()
    {
        using var board = TestBoard.Create();
        var story = board.Create("story", "Cards");
        board.Apply(new AddCriterion(story.Id, "Visa works") { Id = "c1" });
        var move = new TransitionIssue(story.Id, "done").Apply(board.Snapshot, new OperationContext(TestBoard.Ada.Name, TestBoard.Start));

        var found = Assert.Single(DoneReadiness.Check(board.Snapshot, move));
        Assert.Equal(story.Id, found.Issue.Id);
        Assert.Equal("Criterion: Visa works", found.Missing[0]);
        Assert.Equal(7, found.Missing.Count);

        // Warn only asks: the change itself goes through.
        board.Apply(new TransitionIssue(story.Id, "done"));
        Assert.Equal("done", board.Get(story.Id).Status);

        // Moving between other statuses, or with the check off, asks nothing.
        var other = board.Create("story", "Other");
        Assert.Empty(DoneReadiness.Check(board.Snapshot, new TransitionIssue(other.Id, "inprogress").Apply(board.Snapshot, new OperationContext(null, TestBoard.Start))));
        board.Apply(new SetDoneCheck(DoneCheck.Off));
        Assert.Empty(DoneReadiness.Check(board.Snapshot, new TransitionIssue(other.Id, "done").Apply(board.Snapshot, new OperationContext(null, TestBoard.Start))));
    }

    [Fact]
    public void Block_refuses_done_until_everything_is_ticked()
    {
        using var board = TestBoard.Create();
        board.Apply(new SetDoneCheck(DoneCheck.Block));
        var story = board.Create("story", "Cards");
        board.Apply(new AddCriterion(story.Id, "Visa works") { Id = "c1" });

        var refused = Assert.Throws<BoardRuleException>(() => board.Apply(new TransitionIssue(story.Id, "done")));
        Assert.Contains("Visa works", refused.Message);
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(story.Id, IssueFields.Status, new FieldValue.Text("done"))));

        board.Apply(new CheckCriterion(story.Id, "c1", true));
        foreach (var item in board.Snapshot.Project.DefinitionOfDone)
            board.Apply(new CheckDoneItem(story.Id, item.Id, true));
        board.Apply(new TransitionIssue(story.Id, "done"));
        Assert.Equal("done", board.Get(story.Id).Status);

        // Creating an issue straight into Done (imports) isn't a move into Done.
        board.Apply(new CreateIssue("story", "Imported") { Id = "imp", Status = "done" });
        Assert.Equal("done", board.Get("imp").Status);
    }

    [Fact]
    public void People_get_roles()
    {
        using var board = TestBoard.Create();
        var me = Assert.Single(board.Snapshot.Project.People);

        board.Apply(new SetPersonRole(me.Id, " Scrum Master "));
        Assert.Equal("Scrum Master", board.Snapshot.Project.Person(me.Id)!.Role);
        board.Apply(new SetPersonRole(me.Id, ""));
        Assert.Null(board.Snapshot.Project.Person(me.Id)!.Role);
    }

    [Fact]
    public void Search_finds_open_criteria_and_definition_of_done()
    {
        using var board = TestBoard.Create();
        var a = board.Create("story", "A");
        var b = board.Create("story", "B");
        board.Apply(new AddCriterion(a.Id, "Works offline") { Id = "c1" });
        foreach (var item in board.Snapshot.Project.DefinitionOfDone)
            board.Apply(new CheckDoneItem(b.Id, item.Id, true));

        Assert.Equal([a.Key], board.Find("openCriteria > 0"));
        Assert.Equal([a.Key], board.Find("criteria ~ offline"));
        Assert.Equal([b.Key], board.Find("criteria is empty"));
        Assert.Equal([b.Key], board.Find("openDone = 0"));
    }

    [Fact]
    public void Csv_has_criteria_definition_of_done_and_custom_fields()
    {
        using var board = TestBoard.Create();
        var story = board.Create("story", "Cards");
        board.Apply(new AddCriterion(story.Id, "Visa works") { Id = "c1" });
        board.Apply(new AddCriterion(story.Id, "Refunds work") { Id = "c2" });
        board.Apply(new CheckCriterion(story.Id, "c1", true));
        board.Apply(new CheckDoneItem(story.Id, "review", true));
        board.Apply(new EditIssue(story.Id, IssueFields.Custom("value"), new FieldValue.Text("8")));

        var rows = Csv.Read(BoardExport.ToCsv(board.Snapshot));
        var header = rows[0].ToList();
        var row = rows.Single(r => r[0] == story.Key);

        Assert.Equal("[x] Visa works\n[ ] Refunds work", row[header.IndexOf("Acceptance criteria")]);
        Assert.Equal("1/6", row[header.IndexOf("Definition of Done")]);
        Assert.Equal("8", row[header.IndexOf("Business value")]);
    }
}
