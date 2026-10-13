using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Search;
using Xunit;

namespace DevHub.Board.Core.Tests;

public class QueryParserTests
{
    [Fact]
    public void Parses_and_or_not_with_the_usual_precedence()
    {
        var query = QueryParser.Parse("type = bug OR type = task AND NOT status = Done");

        var or = Assert.IsType<QueryNode.Or>(query.Where);
        Assert.IsType<QueryNode.Compare>(or.Left);
        var and = Assert.IsType<QueryNode.And>(or.Right);
        Assert.IsType<QueryNode.Not>(and.Right);
    }

    [Fact]
    public void Parses_in_is_empty_functions_quotes_and_order()
    {
        var query = QueryParser.Parse("labels in (ui, \"needs design\") and assignee = currentUser() and points is not empty " +
                                      "and sprint in openSprints() order by priority desc, created");

        Assert.Equal([new QueryOrder("priority", true), new QueryOrder("created", false)], query.OrderBy);
        var values = Values(query.Where!).ToList();
        Assert.Contains(new QueryValue.Literal("needs design"), values);
        Assert.Contains(values, v => v is QueryValue.Function { Name: "currentUser" });
        Assert.Contains(values, v => v is QueryValue.Function { Name: "openSprints" });
        Assert.Contains("points", Fields(query.Where!));
    }

    private static IEnumerable<QueryValue> Values(QueryNode node) => node switch
    {
        QueryNode.And a => Values(a.Left).Concat(Values(a.Right)),
        QueryNode.Or o => Values(o.Left).Concat(Values(o.Right)),
        QueryNode.Not n => Values(n.Inner),
        QueryNode.Compare c => [c.Value],
        QueryNode.In i => i.Values,
        _ => [],
    };

    private static IEnumerable<string> Fields(QueryNode node) => node switch
    {
        QueryNode.And a => Fields(a.Left).Concat(Fields(a.Right)),
        QueryNode.Empty e => [e.Field],
        _ => [],
    };

    [Fact]
    public void An_empty_query_matches_everything()
    {
        Assert.Null(QueryParser.Parse("  ").Where);
        Assert.Equal([new QueryOrder("rank", false)], QueryParser.Parse("ORDER BY rank").OrderBy);
    }

    [Theory]
    [InlineData("status =", "missing")]
    [InlineData("status Done", "operator")]
    [InlineData("(status = Done", "closed")]
    [InlineData("summary ~ \"open", "quote")]
    [InlineData("status = Done extra", "Expected AND")]
    [InlineData("status in ()", "empty")]
    [InlineData("status is done", "EMPTY")]
    [InlineData("status = Done #", "Unexpected")]
    public void Errors_say_what_and_where(string text, string message)
    {
        var e = Assert.Throws<QueryException>(() => QueryParser.Parse(text));
        Assert.Contains(message, e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(e.Position, 0, text.Length);
    }

    [Theory]
    [InlineData("ui", "ui")]
    [InlineData("In Progress", "\"In Progress\"")]
    [InlineData("and", "\"and\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    public void Quotes_values_that_need_it(string value, string quoted)
    {
        Assert.Equal(quoted, QueryParser.Quote(value));
        Assert.Equal(value, ((QueryValue.Literal)((QueryNode.Compare)QueryParser.Parse("summary = " + quoted).Where!).Value).Text);
    }
}

public class QueryCompilerTests : IDisposable
{
    private readonly TestBoard _board = TestBoard.Create();
    private readonly Issue _epic, _login, _crash, _sub, _old;

    public QueryCompilerTests()
    {
        _board.Apply(new AddPerson("Grace Hopper", "grace@example.com") { Id = "grace" });
        _board.Apply(new AddComponent("UI") { Id = "ui" });
        _board.Apply(new CreateVersion("1.0") { Id = "v1" });
        _board.Apply(new AddCustomField("Team", FieldType.Select) { Id = "team", Options = ["Platform", "Apps"] });
        _board.AddSprint("s1", "Sprint 1", SprintState.Active);
        _board.AddSprint("s2", "Sprint 2", SprintState.Future, 2);

        _old = _board.Create("task", "Old chore");                                  // DEV-1, created at Start
        _board.Time.Advance(TimeSpan.FromDays(10));
        _epic = _board.Create("epic", "Accounts");                                   // DEV-2
        _login = _board.Create("story", "Login form", parent: _epic.Id);            // DEV-3
        _crash = _board.Create("bug", "Crash on start");                             // DEV-4
        _sub = _board.Create("subtask", "Layout of the form", parent: _login.Id);   // DEV-5

        var me = _board.Session.CurrentPersonId!;
        _board.Apply(new BulkEdit([_login.Id], [
            (IssueFields.Assignee, new FieldValue.Text(me)), (IssueFields.StoryPoints, new FieldValue.Number(5)),
            (IssueFields.Labels, new FieldValue.Items(["auth", "ui"])), (IssueFields.Components, new FieldValue.Items(["ui"])),
            (IssueFields.FixVersions, new FieldValue.Items(["v1"])), (IssueFields.Sprint, new FieldValue.Text("s1")),
            (IssueFields.Priority, new FieldValue.Text("high")), (IssueFields.Custom("team"), new FieldValue.Text("Apps")),
            (IssueFields.OriginalEstimate, new FieldValue.Minutes(480)), (IssueFields.Due, new FieldValue.Date(new DateOnly(2026, 10, 30))),
        ]));
        _board.Apply(new BulkEdit([_crash.Id], [(IssueFields.Assignee, new FieldValue.Text("grace")), (IssueFields.StoryPoints, new FieldValue.Number(2)),
            (IssueFields.Priority, new FieldValue.Text("highest"))]));
        _board.Apply(new TransitionIssue(_old.Id, "done"));
        _board.Apply(new AddComment(_crash.Id, "Happens after the splash screen"));
    }

    public void Dispose() => _board.Dispose();

    [Theory]
    [InlineData("type = bug", "DEV-4")]
    [InlineData("type = Bug", "DEV-4")]
    [InlineData("issuetype in (bug, story)", "DEV-3,DEV-4")]
    [InlineData("status = Done", "DEV-1")]
    [InlineData("status != Done AND type != epic AND type != subtask", "DEV-3,DEV-4")]
    [InlineData("statusCategory = \"To Do\" and type = task", "")]
    [InlineData("statusCategory = done", "DEV-1")]
    [InlineData("resolution = Done", "DEV-1")]
    [InlineData("resolution is empty and type = task", "")]
    [InlineData("assignee = currentUser()", "DEV-3")]
    [InlineData("assignee = \"grace hopper\"", "DEV-4")]
    [InlineData("assignee = grace@example.com", "DEV-4")]
    [InlineData("assignee is empty", "DEV-1,DEV-2,DEV-5")]
    [InlineData("reporter = currentUser() and key = DEV-1", "DEV-1")]
    [InlineData("labels = ui", "DEV-3")]
    [InlineData("labels in (AUTH, other)", "DEV-3")]
    [InlineData("labels not in (auth) and labels is not empty", "")]
    [InlineData("component = UI", "DEV-3")]
    [InlineData("fixVersion = \"1.0\"", "DEV-3")]
    [InlineData("sprint = \"Sprint 1\"", "DEV-3,DEV-5")]
    [InlineData("sprint in openSprints()", "DEV-3,DEV-5")]
    [InlineData("sprint in futureSprints()", "")]
    [InlineData("sprint is empty and type = bug", "DEV-4")]
    [InlineData("parent = DEV-3", "DEV-5")]
    [InlineData("epic = DEV-2", "DEV-3,DEV-5")]
    [InlineData("summary ~ form", "DEV-3,DEV-5")]
    [InlineData("summary ~ \"form login\"", "DEV-3")]
    [InlineData("summary !~ form and type = story", "")]
    [InlineData("comment ~ splash", "DEV-4")]
    [InlineData("text ~ splash", "DEV-4")]
    [InlineData("points > 2", "DEV-3")]
    [InlineData("points >= 2", "DEV-3,DEV-4")]
    [InlineData("points = 5", "DEV-3")]
    [InlineData("storyPoints is empty", "DEV-1,DEV-2,DEV-5")]
    [InlineData("originalEstimate >= 1d", "DEV-3")]
    [InlineData("remainingEstimate = 8h", "DEV-3")]
    [InlineData("priority = highest", "DEV-4")]
    [InlineData("priority > medium", "DEV-3,DEV-4")]
    [InlineData("priority >= high and priority < highest", "DEV-3")]
    [InlineData("key > DEV-3", "DEV-4,DEV-5")]
    [InlineData("key in (dev-1, DEV-4)", "DEV-1,DEV-4")]
    [InlineData("project = DEV and key = DEV-2", "DEV-2")]
    [InlineData("project = OTHER", "")]
    [InlineData("created < -5d", "DEV-1")]
    [InlineData("created >= -1d", "DEV-2,DEV-3,DEV-4,DEV-5")]
    [InlineData("created = 2026-10-13", "DEV-1")]
    [InlineData("created >= startOfDay() and type = bug", "DEV-4")]
    [InlineData("created < startOfWeek()", "DEV-1")]
    [InlineData("resolved >= startOfDay()", "DEV-1")]
    [InlineData("due <= 2026-10-30", "DEV-3")]
    [InlineData("due = 2026-10-30", "DEV-3")]
    [InlineData("Team = Apps", "DEV-3")]
    [InlineData("team is empty and type = bug", "DEV-4")]
    [InlineData("(type = bug or type = story) and not assignee = currentUser()", "DEV-4")]
    public void Finds(string query, string keys) =>
        Assert.Equal(keys.Length == 0 ? [] : keys.Split(','), _board.Find(query).Order(StringComparer.Ordinal));

    [Theory]
    [InlineData("type = bug or type = story order by priority", "DEV-4,DEV-3")]
    [InlineData("type = bug or type = story order by priority desc", "DEV-3,DEV-4")]
    [InlineData("points is not empty order by points desc", "DEV-3,DEV-4")]
    [InlineData("type != epic order by key desc", "DEV-5,DEV-4,DEV-3,DEV-1")]
    [InlineData("type != epic", "DEV-1,DEV-3,DEV-4,DEV-5")]
    [InlineData("order by created desc, key", "DEV-2,DEV-3,DEV-4,DEV-5,DEV-1")]
    public void Orders(string query, string keys) => Assert.Equal(keys.Split(','), _board.Find(query));

    [Theory]
    [InlineData("colour = red", "no field")]
    [InlineData("status = Nope", "no status")]
    [InlineData("assignee = nobody", "no person")]
    [InlineData("Team = Web", "choices")]
    [InlineData("points = lots", "number")]
    [InlineData("created > yesterday", "isn't a date")]
    [InlineData("status ~ Done", "contains")]
    [InlineData("status > Done", "can't be compared")]
    [InlineData("parent = DEV-99", "no issue")]
    [InlineData("order by colour", "ordered")]
    [InlineData("type = currentUser()", "can't be used")]
    public void Unknown_fields_and_values_are_errors(string query, string message)
    {
        var e = Assert.Throws<QueryException>(() => _board.Find(query));
        Assert.Contains(message, e.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public class SavedFilterTests
{
    [Fact]
    public void Filters_are_saved_changed_and_deleted()
    {
        using var board = TestBoard.Create();

        board.Apply(new SaveFilter("My bugs", "type = bug and assignee = currentUser()") { Id = "f1" });
        Assert.Equal("My bugs", board.Snapshot.Filters["f1"].Name);
        Assert.Equal(board.Session.CurrentPersonId, board.Snapshot.Filters["f1"].Owner);

        board.Apply(new SaveFilter("Open bugs", "type = bug and statusCategory != done") { Id = "f1" });
        Assert.Equal("Open bugs", board.Reload().Filters["f1"].Name);
        Assert.Throws<BoardRuleException>(() => board.Apply(new SaveFilter("Broken", "colour = red")));
        Assert.Throws<BoardRuleException>(() => board.Apply(new SaveFilter("open BUGS", "type = bug")));

        board.Apply(new DeleteFilter("f1"));
        Assert.Empty(board.Snapshot.Filters);
    }
}
