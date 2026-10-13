using System.Text;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using Xunit;

namespace DevHub.Board.Core.Tests;

public class CreateBoardTests
{
    [Fact]
    public void A_new_board_has_the_presets_definitions_and_its_creator()
    {
        using var board = TestBoard.Create(BoardPreset.Scrum);
        var snapshot = board.Snapshot;

        Assert.Equal("DEV", snapshot.Project.Key);
        Assert.Equal(["epic", "story", "task", "bug", "subtask"], snapshot.Project.IssueTypes.Select(t => t.Id));
        Assert.Equal(["To Do", "In Progress", "In Review", "Done"], snapshot.Project.Statuses.Select(s => s.Name));
        var definition = Assert.Single(snapshot.Boards.Values);
        Assert.Equal(BoardKind.Scrum, definition.Kind);
        Assert.Equal(["To Do", "In Progress", "In Review", "Done"], definition.Columns.Select(c => c.Name));
        var workflow = Assert.Single(snapshot.Workflows.Values);
        Assert.Equal("todo", workflow.InitialStatus);
        var person = Assert.Single(snapshot.Project.People);
        Assert.Equal("Ada Lovelace", person.Name);
        Assert.Equal(person.Id, board.Session.CurrentPersonId);
        Assert.Empty(snapshot.Problems);
    }

    [Fact]
    public void The_empty_preset_has_no_columns()
    {
        using var board = TestBoard.Create(BoardPreset.Empty);

        Assert.Empty(Assert.Single(board.Snapshot.Boards.Values).Columns);
        Assert.Equal(2, board.Snapshot.Project.Statuses.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dev 1")]
    [InlineData("TOOLONGKEY1")]
    public void Bad_keys_are_refused(string key) =>
        Assert.Throws<BoardRuleException>(() => new CreateBoard(key, "x", BoardPreset.Kanban, TestBoard.Ada)
            .Build(new OperationContext(null, TestBoard.Start)));

    [Fact]
    public void Files_are_written_with_stable_formatting()
    {
        using var board = TestBoard.Create();
        var text = Encoding.UTF8.GetString(board.Snapshot.Files[BoardPaths.Project]);

        Assert.DoesNotContain("\r", text);
        Assert.EndsWith("}\n", text);
        Assert.StartsWith("{\n  \"schema\": 1,\n  \"key\": \"DEV\"", text);
        Assert.False(board.Snapshot.Files[BoardPaths.Project].AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
    }
}

public class IssueOperationTests
{
    [Fact]
    public void Issues_get_consecutive_keys_the_first_status_and_the_default_priority()
    {
        using var board = TestBoard.Create();

        var one = board.Create("story", "Login form");
        var two = board.Create("bug", "Crash on start");

        Assert.Equal("DEV-1", one.Key);
        Assert.Equal("DEV-2", two.Key);
        Assert.Equal(3, board.Snapshot.Project.NextNumber);
        Assert.Equal("todo", one.Status);
        Assert.Equal("medium", one.Priority);
        Assert.Equal(board.Session.CurrentPersonId, one.Reporter);
        Assert.Equal(TestBoard.Start, one.Created);
        Assert.True(string.CompareOrdinal(one.Rank, two.Rank) < 0, "new issues go last");
        Assert.Same(one.Key, board.Snapshot.IssueByKey("dev-1")!.Key);
    }

    [Fact]
    public void Issues_survive_a_reload_from_disk()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login form");
        board.Apply(new EditIssue(issue.Id, IssueFields.StoryPoints, new FieldValue.Number(5)));

        var reloaded = board.Reload().Issue(issue.Id)!;

        Assert.Equal("Login form", reloaded.Summary);
        Assert.Equal(5, reloaded.StoryPoints);
        Assert.Equal(issue.Rank, reloaded.Rank);
    }

    [Fact]
    public void A_summary_is_required()
    {
        using var board = TestBoard.Create();

        Assert.Throws<BoardRuleException>(() => board.Apply(new CreateIssue("story", "  ")));
        Assert.Empty(board.Snapshot.Issues);
        Assert.Equal(1, board.Snapshot.Project.NextNumber);
    }

    [Fact]
    public void Hierarchy_epics_hold_standard_issues_which_hold_sub_tasks()
    {
        using var board = TestBoard.Create();
        var epic = board.Create("epic", "Accounts");
        var story = board.Create("story", "Login", parent: epic.Id);
        var sub = board.Create("subtask", "Layout", parent: story.Id);

        Assert.Equal(epic.Id, story.Parent);
        Assert.Equal(epic.Id, board.Snapshot.EpicOf(sub)!.Id);
        Assert.Equal([story.Id], board.Snapshot.ChildrenOf(epic.Id).Select(i => i.Id));

        Assert.Throws<BoardRuleException>(() => board.Apply(new CreateIssue("subtask", "Orphan")));
        Assert.Throws<BoardRuleException>(() => board.Apply(new CreateIssue("subtask", "Under an epic") { Parent = epic.Id }));
        Assert.Throws<BoardRuleException>(() => board.Apply(new CreateIssue("story", "Under a story") { Parent = story.Id }));
        Assert.Throws<BoardRuleException>(() => board.Apply(new CreateIssue("epic", "Under an epic") { Parent = epic.Id }));
    }

    [Fact]
    public void Editing_a_field_writes_a_history_line()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");
        board.Time.Advance(TimeSpan.FromHours(1));

        board.Apply(new EditIssue(issue.Id, IssueFields.StoryPoints, new FieldValue.Number(3)));
        board.Apply(new EditIssue(issue.Id, IssueFields.StoryPoints, new FieldValue.Number(5)));

        var history = board.Snapshot.HistoryOf(issue.Id);
        Assert.Equal(["created", "storyPoints", "storyPoints"], history.Select(h => h.Field));
        Assert.Equal((null, "3"), (history[1].From, history[1].To));
        Assert.Equal(("3", "5"), (history[2].From, history[2].To));
        Assert.Equal(board.Session.CurrentPersonId, history[2].Who);
        Assert.Equal(TestBoard.Start.AddHours(1), history[2].At);
        Assert.Equal(TestBoard.Start.AddHours(1), board.Get(issue.Id).Updated);
    }

    [Fact]
    public void Setting_a_field_to_its_value_writes_nothing()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");
        var version = board.Snapshot.Version;

        var change = board.Apply(new EditIssue(issue.Id, IssueFields.Summary, new FieldValue.Text("Login")));

        Assert.True(change.IsEmpty);
        Assert.Equal(version, board.Snapshot.Version);
    }

    [Fact]
    public void Field_values_are_checked()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");

        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(issue.Id, IssueFields.Priority, new FieldValue.Text("urgent"))));
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(issue.Id, IssueFields.Assignee, new FieldValue.Text("nobody"))));
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(issue.Id, IssueFields.StoryPoints, new FieldValue.Number(-1))));
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(issue.Id, IssueFields.Labels, new FieldValue.Items(["two words"]))));
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(issue.Id, IssueFields.Status, new FieldValue.Text("done"))));
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(issue.Id, IssueFields.Sprint, new FieldValue.Text("nosuch"))));
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(issue.Id, "custom.nosuch", new FieldValue.Text("x"))));
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(issue.Id, "nosuch", new FieldValue.Text("x"))));
    }

    [Fact]
    public void Labels_are_unique_and_sorted()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");

        board.Apply(new EditIssue(issue.Id, IssueFields.Labels, new FieldValue.Items(["ui", "Auth", "UI", "auth"])));

        Assert.Equal(["Auth", "ui"], board.Get(issue.Id).Labels);
    }

    [Fact]
    public void An_original_estimate_also_sets_an_empty_remaining_estimate()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");

        board.Apply(new EditIssue(issue.Id, IssueFields.OriginalEstimate, new FieldValue.Minutes(480)));

        Assert.Equal(480, board.Get(issue.Id).Time.OriginalMinutes);
        Assert.Equal(480, board.Get(issue.Id).Time.RemainingMinutes);
    }

    [Fact]
    public void Changing_type_keeps_the_hierarchy_valid()
    {
        using var board = TestBoard.Create();
        var epic = board.Create("epic", "Accounts");
        var story = board.Create("story", "Login", parent: epic.Id);
        board.Create("subtask", "Layout", parent: story.Id);

        board.Apply(new EditIssue(story.Id, IssueFields.Type, new FieldValue.Text("bug")));
        Assert.Equal("bug", board.Get(story.Id).Type);

        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(epic.Id, IssueFields.Type, new FieldValue.Text("story"))));
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(story.Id, IssueFields.Type, new FieldValue.Text("epic"))));
    }

    [Fact]
    public void Custom_fields_are_checked_against_their_type()
    {
        using var board = TestBoard.Create();
        var project = board.Snapshot.Project with
        {
            CustomFields =
            [
                new FieldDefinition { Id = "team", Name = "Team", Type = FieldType.Select, Options = ["Platform", "Apps"] },
                new FieldDefinition { Id = "cost", Name = "Cost", Type = FieldType.Number },
            ],
        };
        board.WriteRaw(BoardPaths.Project, project);
        var issue = board.Create("story", "Login");

        board.Apply(new EditIssue(issue.Id, IssueFields.Custom("team"), new FieldValue.Text("Apps")));
        board.Apply(new EditIssue(issue.Id, IssueFields.Custom("cost"), new FieldValue.Text("12.5")));

        Assert.Equal("Apps", board.Get(issue.Id).Custom["team"]);
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(issue.Id, IssueFields.Custom("team"), new FieldValue.Text("Web"))));
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(issue.Id, IssueFields.Custom("cost"), new FieldValue.Text("lots"))));
        Assert.Equal("custom.team", board.Snapshot.HistoryOf(issue.Id)[1].Field);
    }

    [Fact]
    public void A_transition_to_done_sets_a_resolution_and_leaving_done_clears_it()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");
        board.Time.Advance(TimeSpan.FromDays(1));

        board.Apply(new TransitionIssue(issue.Id, "done"));
        var done = board.Get(issue.Id);
        Assert.Equal("done", done.Resolution);
        Assert.Equal(TestBoard.Start.AddDays(1), done.Resolved);
        Assert.True(board.Snapshot.IsDone(done));

        board.Apply(new TransitionIssue(issue.Id, "inprogress"));
        var reopened = board.Get(issue.Id);
        Assert.Null(reopened.Resolution);
        Assert.Null(reopened.Resolved);

        board.Apply(new TransitionIssue(issue.Id, "done") { Resolution = "wontdo" });
        Assert.Equal("wontdo", board.Get(issue.Id).Resolution);

        var fields = board.Snapshot.HistoryOf(issue.Id).Select(h => h.Field).ToList();
        Assert.Contains("status", fields);
        Assert.Contains("resolution", fields);
    }

    [Fact]
    public void A_workflow_can_refuse_a_transition()
    {
        using var board = TestBoard.Create();
        var workflow = board.Snapshot.Workflows.Values.Single() with
        {
            AnyToAny = false,
            Transitions = [new WorkflowTransition { From = "todo", To = "inprogress" }, new WorkflowTransition { To = "done" }],
        };
        board.WriteRaw(BoardPaths.Workflow(workflow.Id), workflow);
        var issue = board.Create("story", "Login");

        Assert.Throws<BoardRuleException>(() => board.Apply(new TransitionIssue(issue.Id, "inreview")));
        board.Apply(new TransitionIssue(issue.Id, "inprogress"));
        board.Apply(new TransitionIssue(issue.Id, "done"));   // global transition
        Assert.Throws<BoardRuleException>(() => board.Apply(new TransitionIssue(issue.Id, "done")));
    }

    [Fact]
    public void Ranking_puts_an_issue_between_two_others_and_rewrites_only_it()
    {
        using var board = TestBoard.Create();
        var a = board.Create("story", "A");
        var b = board.Create("story", "B");
        var c = board.Create("story", "C");

        var change = board.Apply(new RankIssue(c.Id, a.Id, b.Id));

        Assert.Equal([a.Id, c.Id, b.Id], board.Snapshot.Issues.Values.OrderBy(i => i.Rank, StringComparer.Ordinal).Select(i => i.Id));
        Assert.Single(change.Files.Keys, k => k.StartsWith(BoardPaths.IssuesFolder));

        board.Apply(new RankIssue(b.Id, null, a.Id));
        Assert.Equal([b.Id, a.Id, c.Id], board.Snapshot.Issues.Values.OrderBy(i => i.Rank, StringComparer.Ordinal).Select(i => i.Id));

        board.Apply(new RankIssue(b.Id, c.Id, null));
        Assert.Equal([a.Id, c.Id, b.Id], board.Snapshot.Issues.Values.OrderBy(i => i.Rank, StringComparer.Ordinal).Select(i => i.Id));
        Assert.DoesNotContain(board.Snapshot.HistoryOf(b.Id), h => h.Field == IssueFields.Rank);
    }

    [Fact]
    public void Duplicate_ranks_are_rebalanced()
    {
        using var board = TestBoard.Create();
        var a = board.Create("story", "A");
        var b = board.Create("story", "B");
        var c = board.Create("story", "C");
        // Same rank on a and b (as after a merge).
        var broken = board.Get(b.Id) with { Rank = board.Get(a.Id).Rank };
        board.WriteRaw(BoardPaths.Issue(b.Id), broken);

        board.Apply(new RankIssue(c.Id, a.Id, b.Id));

        var order = board.Snapshot.Issues.Values.OrderBy(i => i.Rank, StringComparer.Ordinal).Select(i => i.Id).ToList();
        Assert.Equal([a.Id, c.Id, b.Id], order);
        Assert.Equal(3, board.Snapshot.Issues.Values.Select(i => i.Rank).Distinct().Count());
    }

    [Fact]
    public void Deleting_an_issue_removes_its_files_and_links_to_it()
    {
        using var board = TestBoard.Create();
        var epic = board.Create("epic", "Accounts");
        var story = board.Create("story", "Login", parent: epic.Id);
        var other = board.Create("task", "Other");
        board.Apply(new AddLink(other.Id, "blocks", epic.Id));
        board.Apply(new AddAttachment(epic.Id, "notes.txt", "hi"u8.ToArray()));

        board.Apply(new DeleteIssue(epic.Id));

        Assert.Null(board.Snapshot.Issue(epic.Id));
        Assert.Null(board.Get(story.Id).Parent);
        Assert.Empty(board.Get(other.Id).Links);
        Assert.DoesNotContain(board.Snapshot.Files.Keys, k => k.Contains(epic.Id));
        Assert.Null(board.Reload().Issue(epic.Id));
    }

    [Fact]
    public void Sub_tasks_are_deleted_with_their_parent_only_when_asked()
    {
        using var board = TestBoard.Create();
        var story = board.Create("story", "Login");
        var sub = board.Create("subtask", "Layout", parent: story.Id);

        Assert.Throws<BoardRuleException>(() => board.Apply(new DeleteIssue(story.Id)));
        board.Apply(new DeleteIssue(story.Id, WithSubtasks: true));

        Assert.Empty(board.Snapshot.Issues);
        Assert.DoesNotContain(board.Snapshot.Files.Keys, k => k.Contains(sub.Id));
    }

    [Fact]
    public void Bulk_edit_changes_every_issue_at_once()
    {
        using var board = TestBoard.Create();
        var a = board.Create("story", "A");
        var b = board.Create("story", "B");

        var change = board.Apply(new BulkEdit([a.Id, b.Id], [(IssueFields.StoryPoints, new FieldValue.Number(2)), (IssueFields.Priority, new FieldValue.Text("high"))]));

        Assert.All(new[] { board.Get(a.Id), board.Get(b.Id) }, i => Assert.Equal((2.0, "high"), (i.StoryPoints!.Value, i.Priority)));
        Assert.Equal("2 issues: story points, priority changed", change.Message);
    }

    [Fact]
    public void Sub_tasks_follow_their_parent_into_a_sprint()
    {
        using var board = TestBoard.Create();
        board.AddSprint("s1", "Sprint 1", SprintState.Active);
        var story = board.Create("story", "Login");
        var sub = board.Create("subtask", "Layout", parent: story.Id);

        board.Apply(new EditIssue(story.Id, IssueFields.Sprint, new FieldValue.Text("s1")));

        Assert.Equal("s1", board.Get(sub.Id).Sprint);
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(sub.Id, IssueFields.Sprint, new FieldValue.Text(null))));
        Assert.Equal("s1", board.Create("subtask", "Tests", parent: story.Id).Sprint);
    }

    [Fact]
    public void Fields_a_newer_version_wrote_are_kept()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");
        var path = BoardPaths.Issue(issue.Id);
        var text = Encoding.UTF8.GetString(board.Snapshot.Files[path]).Replace("\"summary\"", "\"futureField\": {\"a\": 1},\n  \"summary\"");
        board.WriteRaw(path, Encoding.UTF8.GetBytes(text));

        board.Apply(new EditIssue(issue.Id, IssueFields.Summary, new FieldValue.Text("Sign in")));

        var written = Encoding.UTF8.GetString(board.Snapshot.Files[path]);
        Assert.Contains("futureField", written);
        Assert.Contains("Sign in", written);
    }

    [Fact]
    public void A_broken_issue_file_is_reported_not_fatal()
    {
        using var board = TestBoard.Create();
        var good = board.Create("story", "Login");
        board.WriteRaw("issues/broken.json", "{ not json"u8.ToArray());

        var snapshot = board.Reload();

        Assert.NotNull(snapshot.Issue(good.Id));
        Assert.True(snapshot.Problems.ContainsKey("issues/broken.json"));
    }
}

public class IssueDetailTests
{
    [Fact]
    public void Comments_can_be_added_edited_and_deleted()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");

        board.Apply(new AddComment(issue.Id, " Started. ") { Id = "c1" });
        board.Time.Advance(TimeSpan.FromMinutes(5));
        board.Apply(new EditComment(issue.Id, "c1", "Started on this."));

        var comment = Assert.Single(board.Get(issue.Id).Comments);
        Assert.Equal("Started on this.", comment.Body);
        Assert.Equal(board.Session.CurrentPersonId, comment.Author);
        Assert.Equal(TestBoard.Start.AddMinutes(5), comment.Edited);

        board.Apply(new DeleteComment(issue.Id, "c1"));
        Assert.Empty(board.Get(issue.Id).Comments);
        Assert.Throws<BoardRuleException>(() => board.Apply(new AddComment(issue.Id, "  ")));
    }

    [Fact]
    public void Links_are_stored_once_and_seen_from_both_sides()
    {
        using var board = TestBoard.Create();
        var a = board.Create("story", "A");
        var b = board.Create("story", "B");

        board.Apply(new AddLink(a.Id, "blocks", b.Id));

        Assert.Equal("blocks", Assert.Single(board.Get(a.Id).Links).Type);
        Assert.Empty(board.Get(b.Id).Links);
        Assert.Equal(a.Id, Assert.Single(board.Snapshot.IncomingLinks(b.Id)).From.Id);
        Assert.Throws<BoardRuleException>(() => board.Apply(new AddLink(a.Id, "blocks", b.Id)));
        Assert.Throws<BoardRuleException>(() => board.Apply(new AddLink(a.Id, "blocks", a.Id)));

        board.Apply(new RemoveLink(b.Id, "blocks", a.Id));   // from the other side
        Assert.Empty(board.Get(a.Id).Links);
        Assert.Contains(board.Snapshot.HistoryOf(b.Id), h => h.Field == "link");
    }

    [Fact]
    public void Logging_work_lowers_the_remaining_estimate()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");
        board.Apply(new EditIssue(issue.Id, IssueFields.OriginalEstimate, new FieldValue.Minutes(480)));

        board.Apply(new LogWork(issue.Id, 180, TestBoard.Start) { Note = "Layout", Id = "w1" });
        Assert.Equal((180, 300), (board.Get(issue.Id).Time.SpentMinutes, board.Get(issue.Id).Time.RemainingMinutes!.Value));

        board.Apply(new LogWork(issue.Id, 600, TestBoard.Start));
        Assert.Equal(0, board.Get(issue.Id).Time.RemainingMinutes);

        board.Apply(new LogWork(issue.Id, 60, TestBoard.Start) { Remaining = RemainingAdjustment.Set, NewRemainingMinutes = 120 });
        Assert.Equal(120, board.Get(issue.Id).Time.RemainingMinutes);

        board.Apply(new DeleteWorkLog(issue.Id, "w1"));
        Assert.Equal(300, board.Get(issue.Id).Time.RemainingMinutes);
        Assert.Equal(660, board.Get(issue.Id).Time.SpentMinutes);
        Assert.Throws<BoardRuleException>(() => board.Apply(new LogWork(issue.Id, 0, TestBoard.Start)));
    }

    [Fact]
    public void Attachments_are_stored_next_to_the_issue()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");

        board.Apply(new AddAttachment(issue.Id, @"C:\temp\screen:shot.png", [1, 2, 3]) { Id = "f1" });

        var attachment = Assert.Single(board.Get(issue.Id).Attachments);
        Assert.Equal("screen_shot.png", attachment.FileName);
        Assert.Equal([1, 2, 3], board.Snapshot.AttachmentBytes(board.Get(issue.Id), attachment));
        Assert.True(File.Exists(Path.Combine(board.Folder.Path, "attachments", issue.Id, "f1-screen_shot.png")));

        board.Apply(new RemoveAttachment(issue.Id, "f1"));
        Assert.Empty(board.Get(issue.Id).Attachments);
        Assert.False(Directory.Exists(Path.Combine(board.Folder.Path, "attachments")));
        Assert.Throws<BoardRuleException>(() => board.Apply(new AddAttachment(issue.Id, "big.bin", new byte[AddAttachment.MaxBytes + 1])));
    }
}

public class CreateBoardIdTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Not An Id")]
    public void A_blank_or_bad_creator_id_gets_a_real_one(string creatorId)
    {
        var change = new CreateBoard("DEV", "x", BoardPreset.Kanban, new BoardUser("Ada", " "), creatorId)
            .Build(new OperationContext(null, TestBoard.Start));
        var project = BoardJson.Deserialize<BoardProject>(change.Files[BoardPaths.Project]!);

        Assert.True(BoardIds.IsValid(project.People.Single().Id));
        Assert.Null(project.People.Single().Email);
    }
}

public class StartDateTests
{
    [Fact]
    public void Start_and_due_dates_stay_in_order()
    {
        using var board = TestBoard.Create();
        var epic = board.Create("epic", "Accounts");

        board.Apply(new EditIssue(epic.Id, IssueFields.Start, new FieldValue.Date(new DateOnly(2026, 11, 1))));
        board.Apply(new EditIssue(epic.Id, IssueFields.Due, new FieldValue.Date(new DateOnly(2026, 12, 1))));

        Assert.Equal(new DateOnly(2026, 11, 1), board.Get(epic.Id).Start);
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(epic.Id, IssueFields.Due, new FieldValue.Date(new DateOnly(2026, 10, 1)))));
        Assert.Throws<BoardRuleException>(() => board.Apply(new EditIssue(epic.Id, IssueFields.Start, new FieldValue.Date(new DateOnly(2026, 12, 2)))));
        Assert.Contains(board.Snapshot.HistoryOf(epic.Id), h => h.Field == IssueFields.Start);
    }
}
