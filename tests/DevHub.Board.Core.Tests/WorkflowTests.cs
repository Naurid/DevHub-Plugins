using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using Xunit;

namespace DevHub.Board.Core.Tests;

public class WorkflowTests
{
    private static Workflow OnlyWorkflow(TestBoard board) => board.Snapshot.Workflows.Values.Single();

    [Fact]
    public void A_new_status_joins_every_workflow_at_the_end()
    {
        using var board = TestBoard.Create();

        board.Apply(new AddStatus("Blocked", StatusCategory.InProgress) { Id = "blocked" });

        Assert.Equal(StatusCategory.InProgress, board.Snapshot.Project.Status("blocked")!.Category);
        Assert.Equal("blocked", OnlyWorkflow(board).Statuses[^1]);
        Assert.Throws<BoardRuleException>(() => board.Apply(new AddStatus("blocked", StatusCategory.ToDo)));
        Assert.Throws<BoardRuleException>(() => board.Apply(new AddStatus(" ", StatusCategory.ToDo)));
    }

    [Fact]
    public void Renaming_a_status_touches_no_issue()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");

        var change = board.Apply(new RenameStatus("todo", "Open"));

        Assert.Equal("Open", board.Snapshot.Project.Status("todo")!.Name);
        Assert.Equal(["board.json"], change.Files.Keys);
        Assert.Equal("todo", board.Get(issue.Id).Status);
    }

    [Fact]
    public void Moving_a_status_into_done_resolves_its_issues()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");
        board.Apply(new TransitionIssue(issue.Id, "inreview"));

        board.Apply(new SetStatusCategory("inreview", StatusCategory.Done));
        Assert.Equal("done", board.Get(issue.Id).Resolution);

        board.Apply(new SetStatusCategory("inreview", StatusCategory.InProgress));
        Assert.Null(board.Get(issue.Id).Resolution);
    }

    [Fact]
    public void Deleting_a_status_moves_its_issues_and_clears_it_everywhere()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");
        board.Apply(new TransitionIssue(issue.Id, "inreview"));
        board.Apply(new SetTransitions(OnlyWorkflow(board).Id, false,
            [new WorkflowTransition { From = "inprogress", To = "inreview" }, new WorkflowTransition { From = "inreview", To = "done" }]));

        Assert.Throws<BoardRuleException>(() => board.Apply(new DeleteStatus("inreview", null)));
        board.Apply(new DeleteStatus("inreview", "inprogress"));

        Assert.Equal("inprogress", board.Get(issue.Id).Status);
        Assert.Null(board.Snapshot.Project.Status("inreview"));
        Assert.DoesNotContain("inreview", OnlyWorkflow(board).Statuses);
        Assert.Empty(OnlyWorkflow(board).Transitions);
        var definition = board.Snapshot.Boards.Values.Single();
        Assert.Empty(definition.Columns.Single(c => c.Name == "In Review").Statuses);   // the column stays, empty
    }

    [Fact]
    public void The_first_status_of_a_workflow_is_where_issues_start()
    {
        using var board = TestBoard.Create();

        board.Apply(new MoveStatusInWorkflow(OnlyWorkflow(board).Id, "inprogress", 0));

        Assert.Equal("inprogress", board.Create("story", "Login").Status);
    }

    [Fact]
    public void Transitions_must_use_the_workflows_statuses()
    {
        using var board = TestBoard.Create();
        var workflow = OnlyWorkflow(board);
        board.Apply(new AddStatus("Elsewhere", StatusCategory.ToDo) { Id = "elsewhere", Workflow = null });
        board.Apply(new RemoveStatusFromWorkflow(workflow.Id, "elsewhere", null));

        Assert.Throws<BoardRuleException>(() => board.Apply(new SetTransitions(workflow.Id, false, [new WorkflowTransition { To = "elsewhere" }])));
        Assert.Throws<BoardRuleException>(() => board.Apply(new SetTransitions(workflow.Id, false, [new WorkflowTransition { From = "done", To = "done" }])));
    }

    [Fact]
    public void An_issue_type_can_get_its_own_workflow()
    {
        using var board = TestBoard.Create();
        var bug = board.Create("bug", "Crash");
        board.Apply(new TransitionIssue(bug.Id, "inreview"));
        board.Apply(new AddStatus("Triage", StatusCategory.ToDo) { Id = "triage" });
        board.Apply(new CreateWorkflow("Bugs") { Id = "bugs", CopyOf = OnlyWorkflow(board).Id });
        board.Apply(new RemoveStatusFromWorkflow("bugs", "inreview", null));    // no bug uses "bugs" yet
        board.Apply(new MoveStatusInWorkflow("bugs", "triage", 0));

        board.Apply(new AssignWorkflow("bug", "bugs") { StatusMap = new Dictionary<string, string> { ["inreview"] = "inprogress" } });

        Assert.Equal("bugs", board.Snapshot.WorkflowFor("bug")!.Id);
        Assert.Equal("inprogress", board.Get(bug.Id).Status);
        Assert.Equal("triage", board.Create("bug", "Another").Status);
        Assert.NotEqual("bugs", board.Snapshot.WorkflowFor("story")!.Id);
        Assert.Throws<BoardRuleException>(() => board.Apply(new DeleteWorkflow("bugs")));
    }

    [Fact]
    public void A_status_used_by_a_workflow_type_cannot_leave_without_a_target()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");
        board.Apply(new TransitionIssue(issue.Id, "inreview"));

        Assert.Throws<BoardRuleException>(() => board.Apply(new RemoveStatusFromWorkflow(OnlyWorkflow(board).Id, "inreview", null)));
        board.Apply(new RemoveStatusFromWorkflow(OnlyWorkflow(board).Id, "inreview", "done"));

        Assert.Equal("done", board.Get(issue.Id).Status);
        Assert.Equal("done", board.Get(issue.Id).Resolution);
    }

    [Fact]
    public void The_last_workflow_cannot_be_deleted()
    {
        using var board = TestBoard.Create();

        Assert.Throws<BoardRuleException>(() => board.Apply(new DeleteWorkflow(OnlyWorkflow(board).Id)));
    }
}

public class ProjectDefinitionTests
{
    [Fact]
    public void Changing_the_key_renumbers_issues_and_keeps_the_old_keys()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");

        board.Apply(new ChangeProjectKey("app"));

        Assert.Equal("APP", board.Snapshot.Project.Key);
        Assert.Equal("APP-1", board.Get(issue.Id).Key);
        Assert.Equal(["DEV-1"], board.Get(issue.Id).OldKeys);
        Assert.Same(board.Snapshot.IssueByKey("DEV-1"), board.Snapshot.IssueByKey("APP-1"));
        Assert.Equal("APP-2", board.Create("task", "Next").Key);
    }

    [Fact]
    public void Deleting_an_issue_type_converts_its_issues()
    {
        using var board = TestBoard.Create();
        var bug = board.Create("bug", "Crash");

        Assert.Throws<BoardRuleException>(() => board.Apply(new DeleteIssueType("bug", null)));
        Assert.Throws<BoardRuleException>(() => board.Apply(new DeleteIssueType("bug", "epic")));
        board.Apply(new DeleteIssueType("bug", "task"));

        Assert.Equal("task", board.Get(bug.Id).Type);
        Assert.Null(board.Snapshot.Project.IssueType("bug"));
    }

    [Fact]
    public void Custom_issue_types_fit_into_the_hierarchy()
    {
        using var board = TestBoard.Create();
        board.Apply(new AddIssueType("Spike", IssueTypeLevel.Standard, "spike") { Id = "spike" });
        var epic = board.Create("epic", "Research");

        var spike = board.Create("spike", "Try Avalonia 12", parent: epic.Id);

        Assert.Equal(epic.Id, spike.Parent);
    }

    [Fact]
    public void Deleting_a_priority_replaces_it()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");

        board.Apply(new DeletePriority("medium", "low"));

        Assert.Equal("low", board.Get(issue.Id).Priority);
        Assert.Equal("low", board.Snapshot.Project.DefaultPriority);
        Assert.Equal("low", board.Create("story", "Next").Priority);
    }

    [Fact]
    public void Priorities_can_be_reordered_and_added_in_place()
    {
        using var board = TestBoard.Create();

        board.Apply(new AddPriority("Blocker", "highest") { Id = "blocker", Index = 0 });
        board.Apply(new MovePriority("lowest", 1));

        Assert.Equal(["blocker", "lowest", "highest", "high", "medium", "low"], board.Snapshot.Project.Priorities.Select(p => p.Id));
    }

    [Fact]
    public void Resolutions_in_use_need_a_replacement()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");
        board.Apply(new TransitionIssue(issue.Id, "done") { Resolution = "wontdo" });

        Assert.Throws<BoardRuleException>(() => board.Apply(new DeleteResolution("wontdo", null)));
        board.Apply(new DeleteResolution("wontdo", "done"));

        Assert.Equal("done", board.Get(issue.Id).Resolution);
    }

    [Fact]
    public void Custom_fields_lose_values_that_are_no_longer_offered()
    {
        using var board = TestBoard.Create();
        board.Apply(new AddCustomField("Team", FieldType.Select) { Id = "team", Options = ["Platform", "Apps", "Apps", " "] });
        var issue = board.Create("story", "Login");
        board.Apply(new EditIssue(issue.Id, IssueFields.Custom("team"), new FieldValue.Text("Apps")));
        Assert.Equal(["Platform", "Apps"], board.Snapshot.Project.CustomField("team")!.Options);

        board.Apply(new EditCustomField("team", "Squad", ["Platform", "Web"]));
        Assert.False(board.Get(issue.Id).Custom.ContainsKey("team"));

        board.Apply(new EditIssue(issue.Id, IssueFields.Custom("team"), new FieldValue.Text("Web")));
        board.Apply(new DeleteCustomField("team"));
        Assert.Empty(board.Get(issue.Id).Custom);
        Assert.Throws<BoardRuleException>(() => board.Apply(new AddCustomField("Empty choice", FieldType.Select)));
    }

    [Fact]
    public void Components_versions_and_link_types_are_removed_from_issues_with_them()
    {
        using var board = TestBoard.Create();
        board.Apply(new AddComponent("UI") { Id = "ui" });
        board.Apply(new CreateVersion("1.0") { Id = "v1" });
        board.Apply(new CreateVersion("1.1") { Id = "v11" });
        board.Apply(new AddLinkType("causes", "is caused by") { Id = "causes" });
        var a = board.Create("bug", "A");
        var b = board.Create("bug", "B");
        board.Apply(new BulkEdit([a.Id], [(IssueFields.Components, new FieldValue.Items(["ui"])), (IssueFields.FixVersions, new FieldValue.Items(["v1"]))]));
        board.Apply(new AddLink(a.Id, "causes", b.Id));

        board.Apply(new DeleteComponent("ui"));
        board.Apply(new DeleteVersion("v1", MoveTo: "v11"));
        board.Apply(new DeleteLinkType("causes"));

        var after = board.Get(a.Id);
        Assert.Empty(after.Components);
        Assert.Equal(["v11"], after.FixVersions);
        Assert.Empty(after.Links);
        Assert.False(board.Snapshot.Versions.ContainsKey("v1"));
    }

    [Fact]
    public void People_can_be_added_edited_and_removed()
    {
        using var board = TestBoard.Create();
        board.Apply(new AddPerson("Grace Hopper", "grace@example.com") { Id = "grace" });
        var issue = board.Create("story", "Login");
        board.Apply(new EditIssue(issue.Id, IssueFields.Assignee, new FieldValue.Text("grace")));

        Assert.Throws<BoardRuleException>(() => board.Apply(new AddPerson("grace hopper")));
        board.Apply(new EditPerson("grace", "Rear Admiral Hopper", null));
        Assert.Equal("Rear Admiral Hopper", board.Snapshot.Project.Person("grace")!.Name);

        board.Apply(new RemovePerson("grace"));
        Assert.Null(board.Get(issue.Id).Assignee);
        Assert.Throws<BoardRuleException>(() => board.Apply(new RemovePerson(board.Session.CurrentPersonId!)));
    }

    [Fact]
    public void Versions_check_their_dates()
    {
        using var board = TestBoard.Create();

        Assert.Throws<BoardRuleException>(() => board.Apply(new CreateVersion("2.0") { Start = new DateOnly(2026, 11, 1), Release = new DateOnly(2026, 10, 1) }));
        board.Apply(new CreateVersion("2.0") { Id = "v2", Release = new DateOnly(2026, 12, 1) });
        board.Apply(new EditVersion("v2", "2.0", "Big one", null, new DateOnly(2026, 12, 1), Released: true, Archived: false));

        Assert.True(board.Snapshot.Versions["v2"].Released);
    }

    [Fact]
    public void Time_tracking_settings_change_how_durations_read()
    {
        using var board = TestBoard.Create();

        board.Apply(new SetTimeTracking(6, 4));

        Assert.True(WorkDuration.TryParse("1d", board.Snapshot.Project.Settings, out var minutes));
        Assert.Equal(360, minutes);
        Assert.Throws<BoardRuleException>(() => board.Apply(new SetTimeTracking(0, 5)));
    }
}

public class DefaultWorkflowTests
{
    [Fact]
    public void The_default_workflow_is_chosen_not_alphabetical()
    {
        using var board = TestBoard.Create();
        var original = board.Snapshot.DefaultWorkflow!.Id;

        board.Apply(new CreateWorkflow("Aardvark") { Id = "aardvark" });

        Assert.Equal(original, board.Snapshot.WorkflowFor("story")!.Id);
        Assert.Throws<BoardRuleException>(() => board.Apply(new DeleteWorkflow(original)));
    }

    [Fact]
    public void A_new_default_moves_issues_it_has_no_status_for()
    {
        using var board = TestBoard.Create();
        var original = board.Snapshot.DefaultWorkflow!.Id;
        var issue = board.Create("story", "Login");
        board.Apply(new TransitionIssue(issue.Id, "inreview"));
        board.Apply(new CreateWorkflow("Simple") { Id = "simple" });       // only the first status
        board.Apply(new AddStatusToWorkflow("simple", "done"));

        board.Apply(new SetDefaultWorkflow("simple") { StatusMap = new Dictionary<string, string> { ["inreview"] = "done" } });

        Assert.Equal("simple", board.Snapshot.WorkflowFor("story")!.Id);
        Assert.Equal("done", board.Get(issue.Id).Status);
        board.Apply(new DeleteWorkflow(original));
        Assert.Single(board.Snapshot.Workflows);
    }
}
