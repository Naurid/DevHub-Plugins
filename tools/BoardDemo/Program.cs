using System.Diagnostics;
using System.Globalization;
using System.Text;
using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;

// Writes a demo board: seven weeks of a small team building a web shop, replayed through the same operations the
// Board UI uses (so the change log, reports and history are real), and a git repository with branches and commits
// that name the issues (Board.Git's Development section).
//
//   dotnet run --project tools/BoardDemo -- <empty folder> [--me "Your Name"] [--no-git]
//
// The board is in <folder>\board (Open Board… → that folder); the repository is <folder> itself.

var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
if (positional.Count != 1)
{
    Console.WriteLine("Usage: dotnet run --project tools/BoardDemo -- <empty folder> [--me \"Your Name\"] [--no-git]");
    Console.WriteLine("  --me      your name on boards (Board settings; default: the Windows user name), so \"Only my issues\" finds yours");
    Console.WriteLine("  --no-git  only the board, no repository");
    return 1;
}

var root = Path.GetFullPath(positional[0]);
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
{
    Console.Error.WriteLine($"{root} isn't empty: choose a new or empty folder.");
    return 1;
}
var me = Option("--me") ?? Environment.UserName;
var git = args.Contains("--no-git") ? null : DemoGit.TryCreate(root);
if (git is null && !args.Contains("--no-git"))
    Console.WriteLine("git wasn't found: writing the board without a repository.");

var demo = new ShopDemo(Path.Combine(root, "board"), me, git);
await demo.RunAsync();
Console.WriteLine();
Console.WriteLine($"Board written to {Path.Combine(root, "board")}");
if (git is not null)
    Console.WriteLine($"Repository at    {root} ({git.Commits} commits, {git.Branches.Count} branches)");
Console.WriteLine();
Console.WriteLine("In DevHub: Find Action (Ctrl+Shift+A) → Open Board… → choose the board folder above.");
Console.WriteLine($"You are \"{me}\" on it: set the same name in Settings → Board if it differs, so \"Only my issues\" is you.");
return 0;

/// <summary>A clock the replay moves by hand: every change is stamped with the time of its step.</summary>
internal sealed class DemoClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
}

internal sealed record Person(string Id, string Name, string? Email);

internal sealed record DemoIssue(string Id, string Type, string Summary, string Area);

/// <summary>One thing that happens at a time, done by someone: a board operation or a git action.</summary>
internal sealed record Step(DateTimeOffset When, Person Who, Func<BoardSnapshot, IBoardOperation>? Operation, Action<BoardSnapshot>? Git, int Order);

internal sealed class ShopDemo(string folder, string myName, DemoGit? git)
{
    private readonly DemoClock _clock = new();
    private readonly List<Step> _steps = [];
    private readonly Random _random = new(42);
    private readonly DateTime _day0 = DateTime.Today.AddDays(-48);

    // People: you, and a small team. Emails match the git authors.
    private readonly Person _me = new(BoardIds.New(), myName, null);
    private readonly Person _ada = new(BoardIds.New(), "Ada Lovelace", "ada@shop.example");
    private readonly Person _grace = new(BoardIds.New(), "Grace Hopper", "grace@shop.example");
    private readonly Person _alan = new(BoardIds.New(), "Alan Turing", "alan@shop.example");
    private readonly Person _katherine = new(BoardIds.New(), "Katherine Johnson", "katherine@shop.example");
    private readonly Person _edsger = new(BoardIds.New(), "Edsger Dijkstra", "edsger@shop.example");

    // Definitions, with ids chosen here so later steps can name them.
    private readonly string _web = BoardIds.New(), _api = BoardIds.New(), _db = BoardIds.New(), _infra = BoardIds.New(), _mobile = BoardIds.New();
    private readonly string _v10 = BoardIds.New(), _v11 = BoardIds.New(), _v20 = BoardIds.New();
    private readonly string _bugWorkflow = BoardIds.New(), _kanban = BoardIds.New(), _triageBoard = BoardIds.New();
    private const string Platform = "platform", Value = "value", Customer = "customer", Qa = "qa", Triage = "triage";
    private readonly string[] _sprints = Enumerable.Range(0, 6).Select(_ => BoardIds.New()).ToArray();

    private DateTimeOffset At(int day, double hour) => new(_day0.AddDays(day).AddHours(hour));
    private DateOnly Date(int day) => DateOnly.FromDateTime(_day0.AddDays(day));

    private void Do(DateTimeOffset when, Person who, Func<BoardSnapshot, IBoardOperation> operation) =>
        _steps.Add(new Step(when, who, operation, null, _steps.Count));

    private void Do(DateTimeOffset when, Person who, IBoardOperation operation) => Do(when, who, _ => operation);

    private void Git(DateTimeOffset when, Person who, Action<DemoGit, BoardSnapshot> action)
    {
        if (git is not null)
            _steps.Add(new Step(when, who, null, board => action(git.At(when), board), _steps.Count));
    }

    private static string KeyOf(BoardSnapshot board, DemoIssue issue) => board.Issue(issue.Id)!.Key;

    public async Task RunAsync()
    {
        Setup();
        Sprint1();
        Sprint2();
        Sprint3();
        Sprint4();
        Later();

        var ordered = _steps.OrderBy(s => s.When).ThenBy(s => s.Order).ToList();
        if (ordered[^1].When > DateTimeOffset.Now)
            throw new InvalidOperationException("A step is in the future.");

        _clock.Now = ordered[0].When;
        var store = new FolderBoardStore(folder);
        var sessions = new Dictionary<string, BoardSession>
        {
            [_me.Id] = await BoardSession.CreateAsync(store, new CreateBoard("SHOP", "Web Shop", BoardPreset.Scrum, new BoardUser(_me.Name), _me.Id), _clock),
        };
        var done = 0;
        try
        {
            foreach (var step in ordered)
            {
                _clock.Now = step.When;
                if (!sessions.TryGetValue(step.Who.Id, out var session))
                {
                    session = await BoardSession.OpenAsync(store, new BoardUser(step.Who.Name, step.Who.Email), _clock)
                              ?? throw new InvalidOperationException("The board went missing.");
                    sessions[step.Who.Id] = session;
                }
                await session.ReloadAsync();
                if (step.Operation is not null)
                {
                    var operation = step.Operation(session.Snapshot);
                    try
                    {
                        await session.ApplyAsync(operation);
                    }
                    catch (BoardRuleException e)
                    {
                        throw new InvalidOperationException($"Step {step.Order} ({operation.GetType().Name} at {step.When:g} by {step.Who.Name}): {e.Message}", e);
                    }
                }
                step.Git?.Invoke(session.Snapshot);
                if (++done % 100 == 0)
                    Console.Write($"\r{done} / {ordered.Count} steps");
            }
            Console.Write($"\r{done} / {ordered.Count} steps");
            git?.Finish();
        }
        finally
        {
            foreach (var session in sessions.Values)
                session.Dispose();
        }
    }

    // ---- Day 0–2: the project is set up and the backlog written ---------------------------------------------

    private void Setup()
    {
        var t = At(0, 9);
        foreach (var person in new[] { _ada, _grace, _alan, _katherine, _edsger })
            Do(t, _me, new AddPerson(person.Name, person.Email) { Id = person.Id });
        Do(t, _me, new SetPersonRole(_ada.Id, "Product Owner"));
        Do(t, _me, new SetPersonRole(_grace.Id, "Scrum Master"));
        foreach (var person in new[] { _me, _alan, _katherine, _edsger })
            Do(t, _me, new SetPersonRole(person.Id, "Developer"));

        Do(t, _me, new AddComponent("Web", "The storefront: pages, styles, scripts") { Id = _web });
        Do(t, _me, new AddComponent("API", "The HTTP API behind the shop") { Id = _api });
        Do(t, _me, new AddComponent("Database", "Schema, migrations, search index") { Id = _db });
        Do(t, _me, new AddComponent("Infrastructure", "Build, deploy, hosting, CDN") { Id = _infra });
        Do(t, _me, new AddComponent("Mobile", "The iOS and Android apps") { Id = _mobile });

        Do(t, _me, new AddIssueType("Spike", IssueTypeLevel.Standard, "task") { Id = "spike" });
        Do(t, _me, new AddCustomField("Platform", FieldType.Select) { Id = Platform, Options = ["Web", "iOS", "Android", "All"] });
        Do(t, _me, new AddCustomField("Customer", FieldType.Text) { Id = Customer });

        // A QA column (and status) between In Review and Done.
        Do(t, _me, b => new AddColumn(MainBoard(b).Id, "QA") { Index = 3, StatusId = Qa });
        Do(t, _me, b => new MoveStatusInWorkflow(b.Project.DefaultWorkflow!, Qa, 3));

        // Bugs get their own workflow: they start in Triage.
        Do(t, _me, b => new CreateWorkflow("Bug workflow") { Id = _bugWorkflow, CopyOf = b.Project.DefaultWorkflow });
        Do(t, _me, new AddStatus("Triage", StatusCategory.ToDo) { Id = Triage, Workflow = _bugWorkflow });
        Do(t, _me, new MoveStatusInWorkflow(_bugWorkflow, Triage, 0));
        Do(t, _me, new AssignWorkflow("bug", _bugWorkflow));
        Do(t, _me, b => new SetColumnStatuses(MainBoard(b).Id, Column(b, "To Do").Id, ["todo", Triage]));

        // The Scrum board: WIP limits, more quick filters, more on the cards.
        Do(t, _me, b => new SetWipLimits(MainBoard(b).Id, Column(b, "In Progress").Id, null, 4));
        Do(t, _me, b => new SetWipLimits(MainBoard(b).Id, Column(b, "In Review").Id, null, 2));
        Do(t, _me, b => new SetQuickFilter(MainBoard(b).Id, null, "Bugs", "type = bug"));
        Do(t, _me, b => new SetQuickFilter(MainBoard(b).Id, null, "High priority", "priority in (Highest, High)"));
        Do(t, _me, b => new SetQuickFilter(MainBoard(b).Id, null, "Web", "component = Web"));
        Do(t, _me, b => new EditBoardView(MainBoard(b).Id) { CardFields = ["epic", "storyPoints", "priority", "assignee", "labels", "due"] });

        // Two more boards on the same issues.
        Do(t, _me, new CreateBoardView("Team Kanban", BoardKind.Kanban) { Id = _kanban });
        Do(t, _me, new EditBoardView(_kanban)
        {
            Swimlanes = SwimlaneMode.Assignee, HideSubtasks = true, HideDoneAfterDays = 21,
            CardFields = ["epic", "storyPoints", "assignee", "remainingEstimate"],
        });
        Do(t, _me, new CreateBoardView("Bug triage", BoardKind.Kanban) { Id = _triageBoard, Filter = "type = bug" });
        Do(t, _me, new AddColumn(_triageBoard, "Triage") { Index = 0 });
        Do(t, _me, new EditBoardView(_triageBoard) { Swimlanes = SwimlaneMode.Priority, HideEpics = true, CardFields = ["priority", "assignee", "due"] });

        // Releases.
        Do(t, _ada, new CreateVersion("1.0") { Id = _v10, Description = "Customers can sign up, sign in and browse.", Start = Date(3), Release = Date(31) });
        Do(t, _ada, new CreateVersion("1.1") { Id = _v11, Description = "Search, product pages and the cart.", Start = Date(31), Release = Date(58) });
        Do(t, _ada, new CreateVersion("2.0") { Id = _v20, Description = "Checkout and payments: the shop takes money.", Start = Date(59), Release = Date(95) });

        Git(At(0, 9.5), _me, (g, _) => g.Init(_me));

        Do(At(2, 16), _ada, new CreateSprint { Id = _sprints[0], Goal = "Customers can create an account and sign in" });
        Do(At(2, 16), _ada, new CreateSprint { Id = _sprints[1], Goal = "Customers can browse the catalogue" });
        Do(At(2, 16.5), _me, new SaveFilter("My open work", "assignee = currentUser() AND statusCategory != Done ORDER BY priority DESC, rank"));
        Do(At(2, 16.5), _me, new SaveFilter("Open bugs", "type = bug AND resolution is empty ORDER BY priority DESC, created"));
        Do(At(2, 16.5), _ada, new SaveFilter("Release 1.1", "fixVersion = \"1.1\" ORDER BY rank"));
        Do(At(2, 16.5), _ada, new SaveFilter("Stories without points", "type = story AND points is empty AND statusCategory != Done"));
        Do(At(2, 16.5), _ada, new SaveFilter("Overdue", "due < startOfDay() AND statusCategory != Done ORDER BY due"));
    }

    private static BoardDefinition MainBoard(BoardSnapshot board) => board.Boards.Values.First(b => b.Kind == BoardKind.Scrum);
    private static BoardColumn Column(BoardSnapshot board, string name) => MainBoard(board).Columns.First(c => c.Name == name);

    // The epics, with dates for the roadmap. Payments starts before Cart and checkout ends though Cart blocks it.
    private DemoIssue _accounts = null!, _catalogue = null!, _cart = null!, _payments = null!, _orders = null!, _mobileApp = null!;

    // Issues the sprints share.
    private DemoIssue _reset = null!, _rememberMe = null!, _spam = null!, _filters = null!, _searchFlicker = null!;
    private DemoIssue _cartApi = null!, _cartPage = null!, _cartSync = null!, _addToCart = null!;

    private DemoIssue Epic(int day, string summary, string description, int? start, int? due, string? version, params string[] labels)
    {
        var issue = Create(At(day, 9.2), _ada, "epic", summary, description, null, null, "", version, [], labels);
        if (start is not null || due is not null)
            Do(At(day, 9.3), _ada, new BulkEdit([issue.Id],
            [
                (IssueFields.Start, new FieldValue.Date(start is { } s ? Date(s) : null)),
                (IssueFields.Due, new FieldValue.Date(due is { } d ? Date(d) : null)),
            ]));
        return issue;
    }

    private DemoIssue Create(DateTimeOffset when, Person who, string type, string summary, string description, DemoIssue? parent,
        double? points, string component, string? version, IReadOnlyList<(string, FieldValue)> extra, string[]? criteria = null, params string[] labels)
    {
        var id = BoardIds.New();
        var fields = new List<(string Field, FieldValue Value)>();
        if (points is not null)
        {
            fields.Add((IssueFields.StoryPoints, new FieldValue.Number(points)));
            fields.Add((IssueFields.OriginalEstimate, new FieldValue.Minutes((int)(points * 3 * 60))));
        }
        if (component.Length > 0)
            fields.Add((IssueFields.Components, new FieldValue.Items([component])));
        if (version is not null)
            fields.Add((IssueFields.FixVersions, new FieldValue.Items([version])));
        if (labels.Length > 0)
            fields.Add((IssueFields.Labels, new FieldValue.Items(labels)));
        if (type is "story" or "bug")
            fields.Add((IssueFields.Custom(Platform), new FieldValue.Text(component == _mobile ? "All" : "Web")));
        fields.AddRange(extra);
        Do(when, who, new CreateIssue(type, summary) { Id = id, Parent = parent?.Id, Description = description, Fields = fields, Criteria = criteria ?? [] });
        var area = component == _web ? "web" : component == _api ? "api" : component == _db ? "database" : component == _infra ? "infra" : component == _mobile ? "mobile" : "shop";
        return new DemoIssue(id, type, summary, area);
    }

    private DemoIssue Story(int day, string summary, double points, DemoIssue? epic, string component, string? version, int value, params string[] labels) =>
        Create(At(day, 10 + _random.Next(0, 6)), _ada, "story", summary, StoryText(summary), epic, points, component, version,
            [(IssueFields.Custom(Value), new FieldValue.Text(value.ToString(CultureInfo.InvariantCulture)))], StoryCriteria(), labels);

    private DemoIssue Bug(DateTimeOffset when, Person reporter, string summary, string priority, string component, string? version, string? customer, string steps)
    {
        var extra = new List<(string, FieldValue)> { (IssueFields.Priority, new FieldValue.Text(priority)) };
        if (customer is not null)
            extra.Add((IssueFields.Custom(Customer), new FieldValue.Text(customer)));
        return Create(when, reporter, "bug", summary, steps, null, null, component, version, extra);
    }

    private DemoIssue Subtask(DateTimeOffset when, Person who, DemoIssue parent, string summary) =>
        Create(when, who, "subtask", summary, "", parent, null, "", null, []);

    private static string[] StoryCriteria() =>
    [
        "Works on desktop and phone widths",
        "Errors are explained in plain words",
        "Covered by tests (unit + one end-to-end path)",
    ];

    /// <summary>Before Done: every acceptance criterion met and the Definition of Done ticked (standard issues).</summary>
    private void TickEverything(DateTimeOffset when, Person who, DemoIssue issue)
    {
        Do(when, who, new TickAll(issue.Id));
    }

    private static string StoryText(string summary) =>
        $"""
        As a customer I want **{summary.ToLowerInvariant()}**, so that shopping here is quick and pleasant.

        ### Notes
        See the design board for the latest mock-ups. Questions go in the comments below.
        """;

    private void Plan(DateTimeOffset when, DemoIssue issue, int sprint) => Do(when, _ada, new PlanIssue(issue.Id, _sprints[sprint], null, null));

    // The team was put together for this project: it focuses less at first (forming), then settles.
    private static readonly double[] Focus = [0.5, 0.6, 0.7, 0.75, 0.75, 0.8];

    private void StartSprint(int sprint, int day, string? goal = null)
    {
        Do(At(day, 10), _ada, new StartSprint(_sprints[sprint], At(day, 10), At(day + 13, 17)) { Goal = goal });
        // Who's available: everyone ten days, minus a holiday or a course here and there.
        Do(At(day, 10.1), _ada, b =>
        {
            var s = b.Sprints[_sprints[sprint]];
            IReadOnlyList<MemberDays> days =
            [
                new MemberDays { Person = _ada.Id, Days = 6 },   // also Product Owner of another product
                new MemberDays { Person = _grace.Id, Days = 8 },
                new MemberDays { Person = _alan.Id, Days = sprint == 1 ? 7 : 10 },
                new MemberDays { Person = _katherine.Id, Days = 10 },
                new MemberDays { Person = _edsger.Id, Days = sprint == 2 ? 5 : 10 },
            ];
            return new EditSprint(s.Id, s.Name, s.Goal, s.Start, s.End, s.Capacity) { Availability = days, FocusFactor = Focus[sprint] };
        });
    }

    private void CompleteSprint(int sprint, int day)
    {
        Do(At(day, 9.5), _ada, new CompleteSprint(_sprints[sprint], OpenIssuesGoTo.NextSprint) { NextSprint = _sprints[sprint + 1] });
        if (sprint < Retros.Length)
            Do(At(day, 11), _grace, Retros[sprint](this));
    }

    private static readonly Func<ShopDemo, SaveRetrospective>[] Retros =
    [
        d => new SaveRetrospective(new Retrospective
        {
            Sprint = d._sprints[0],
            WentWell = [Note("Everyone met everyone: the kick-off and pairing helped"), Note("Sign-in works end to end")],
            ToImprove = [Note("Roles were unclear for a week"), Note("Stories were too big to finish in a sprint")],
            Problems = [Note("We committed to 21 points with no velocity to go on")],
            Actions =
            [
                new RetroAction { Text = "Split stories over 5 points before planning", Owner = d._ada.Id, Done = true },
                new RetroAction { Text = "Write down who decides what (PO, SM, team)", Owner = d._grace.Id, Done = true },
            ],
            FollowUp = "Checked at the start of the next planning by the Scrum Master.",
        }),
        d => new SaveRetrospective(new Retrospective
        {
            Sprint = d._sprints[1],
            WentWell = [Note("Smaller stories: most of them were done"), Note("Reviews are faster")],
            ToImprove = [Note("The test environment was down twice")],
            Problems = [Note("No one owns the test environment")],
            Actions = [new RetroAction { Text = "Name an owner for the test environment", Owner = d._edsger.Id }],
            FollowUp = "Each daily: is the test environment up? The owner reports.",
        }),
        d => new SaveRetrospective(new Retrospective
        {
            Sprint = d._sprints[2],
            WentWell = [Note("Velocity is stable"), Note("Search shipped early")],
            ToImprove = [Note("Bugs found late, in QA")],
            Problems = [Note("Acceptance criteria were written after the work started")],
            Actions = [new RetroAction { Text = "Acceptance criteria before a story enters a sprint", Owner = d._ada.Id }],
            FollowUp = "The Definition of Ready is checked at planning.",
        }),
    ];

    private static RetroNote Note(string text) => new() { Text = text };

    /// <summary>
    /// Someone works an issue: assigned and In Progress at <paramref name="start"/> (a branch), commits, logged work,
    /// In Review at <paramref name="review"/>, QA, Done at <paramref name="done"/> (the branch merged).
    /// </summary>
    private void Work(DemoIssue issue, Person who, DateTimeOffset start, DateTimeOffset? review = null, DateTimeOffset? done = null,
        double spentFactor = 1, bool keepBranch = false, bool resume = false)
    {
        string BranchOf(BoardSnapshot b) => $"{(issue.Type == "bug" ? "bugfix" : "feature")}/{KeyOf(b, issue)}-{DemoGit.Slug(issue.Summary)}";
        var withGit = issue.Type is not ("epic" or "subtask");
        if (!resume)
        {
            Do(start, who, new EditIssue(issue.Id, IssueFields.Assignee, new FieldValue.Text(who.Id)));
            Do(start.AddMinutes(1), who, new TransitionIssue(issue.Id, "inprogress"));
            if (withGit)
                Git(start.AddMinutes(2), who, (g, b) => g.Branch(BranchOf(b)));
        }
        if (withGit)
            Git(start.AddHours(3), who, (g, b) => g.Commit(BranchOf(b), who, $"{KeyOf(b, issue)}: {(resume ? "continue " + issue.Summary.ToLowerInvariant() : FirstStep(issue))}", issue.Area, issue.Summary));

        var estimate = issue.Type is "subtask" ? 0 : 1;
        if (review is { } r)
        {
            if (withGit)
                Git(r.AddHours(-1), who, (g, b) => g.Commit(BranchOf(b), who, $"{KeyOf(b, issue)} {LastStep(issue)}", issue.Area, issue.Summary));
            if (estimate > 0)
                Do(r.AddMinutes(-5), who, b => new LogWork(issue.Id, Spent(b, issue, 0.6 * spentFactor), start.AddHours(1)) { Note = "Implementation" });
            Do(r, who, new TransitionIssue(issue.Id, "inreview"));
        }
        if (done is { } d)
        {
            var reviewed = review ?? start;
            var qa = reviewed + (d - reviewed) / 2;
            if (review is not null)
                Do(qa, _edsger, new TransitionIssue(issue.Id, Qa));
            if (estimate > 0)
                Do(d.AddMinutes(-5), who, b => new LogWork(issue.Id, Spent(b, issue, 0.4 * spentFactor), reviewed) { Note = "Review fixes and tests", Remaining = RemainingAdjustment.Set, NewRemainingMinutes = 0 });
            TickEverything(d.AddMinutes(-2), who, issue);
            Do(d, review is null ? who : _ada, new TransitionIssue(issue.Id, "done"));
            if (withGit)
                Git(d.AddMinutes(1), who, (g, b) => g.Merge(BranchOf(b), who, $"Merge {BranchOf(b)}: {KeyOf(b, issue)} {issue.Summary}", delete: !keepBranch));
        }
    }

    private int Spent(BoardSnapshot board, DemoIssue issue, double share)
    {
        var estimate = board.Issue(issue.Id)!.Time.OriginalMinutes ?? 240;
        var minutes = estimate * share * (0.7 + _random.NextDouble() * 0.7);
        return Math.Max(15, (int)Math.Round(minutes / 15) * 15);
    }

    private static string FirstStep(DemoIssue issue) => issue.Type == "bug" ? "reproduce in a test" : "first version of " + issue.Summary.ToLowerInvariant();
    private static string LastStep(DemoIssue issue) => issue.Type == "bug" ? "fix and regression test" : "tests and review comments";

    private void Comment(DateTimeOffset when, Person who, DemoIssue issue, string text) => Do(when, who, new AddComment(issue.Id, text));

    // ---- Sprint 1 (days 3–16): accounts ---------------------------------------------------------------------

    private void Sprint1()
    {
        _accounts = Epic(0, "Accounts and sign-in", "Everything about having an account: sign up, sign in, sessions, password reset.\n\nGoal: **no support mails about logging in.**", 3, 30, _v10, "security");
        _catalogue = Epic(0, "Product catalogue", "Browsing and finding products: lists, categories, search, product pages.", 17, 45, _v11);
        _cart = Epic(1, "Cart and checkout", "From \"add to cart\" to a placed order.", 45, 70, _v20);
        _payments = Epic(1, "Payments", "Taking money safely: cards, refunds, receipts.", 60, 92, _v20, "security");
        _orders = Epic(1, "Order history and emails", "What happens after the order: confirmation, history, reordering.", null, null, null);
        _mobileApp = Epic(2, "Mobile app", "The shop on iOS and Android, sharing the API.", 75, 120, null, "mobile");
        Do(At(2, 11), _ada, new AddLink(_cart.Id, "blocks", _payments.Id));
        Do(At(2, 11), _ada, new AddLink(_catalogue.Id, "relates", _cart.Id));
        // Epics move as their work does.
        Do(At(3, 10.1), _ada, new TransitionIssue(_accounts.Id, "inprogress"));
        Do(At(17, 10.1), _ada, new TransitionIssue(_catalogue.Id, "inprogress"));
        Do(At(31, 9.1), _ada, new TransitionIssue(_accounts.Id, "done"));
        Do(At(45, 10.1), _ada, new TransitionIssue(_cart.Id, "inprogress"));

        var signUp = Story(1, "Sign-up form with email and password", 5, _accounts, _web, _v10, 9);
        var hashing = Story(1, "Store passwords with Argon2", 3, _accounts, _api, _v10, 8, "security");
        var login = Story(1, "Login form", 3, _accounts, _web, _v10, 9);
        var sessions = Story(1, "Session cookies and logout", 3, _accounts, _api, _v10, 7, "security");
        _reset = Story(1, "Password reset by email", 5, _accounts, _api, _v10, 6);
        _rememberMe = Story(2, "Remember me on this device", 2, _accounts, _web, _v10, 3);
        var ci = Create(At(1, 14), _edsger, "task", "Set up the CI pipeline", "Build, test and deploy a preview on every push.", null, 2, _infra, _v10, []);
        var spike = Create(At(2, 9), _ada, "spike", "Choose a payment provider", "Compare three providers: fees, payouts, card types, test sandbox.", _payments, 2, "", _v20, []);
        var validation = Subtask(At(3, 11), _katherine, signUp, "Validation messages");
        var confirm = Subtask(At(3, 11), _katherine, signUp, "Email confirmation link");

        foreach (var issue in new[] { signUp, hashing, login, sessions, _reset, ci, spike })
            Plan(At(3, 9), issue, 0);
        StartSprint(0, 3);

        Work(ci, _edsger, At(3, 11), At(4, 15), At(5, 11));
        Work(hashing, _grace, At(3, 11), At(5, 16), At(6, 14), 1.2);
        Work(signUp, _katherine, At(3, 13), At(7, 15), At(8, 16));
        Work(validation, _katherine, At(4, 10), done: At(5, 17));
        Work(confirm, _katherine, At(6, 10), done: At(7, 12));
        Work(spike, _ada, At(4, 9), done: At(9, 12), spentFactor: 1.5);
        Comment(At(9, 12.2), _ada, spike, "Picked **PayFlow**: lowest fees for small shops, a real sandbox, and payouts in two days. Notes are in the wiki.");
        Work(login, _me, At(5, 10), At(9, 11), At(10, 15));
        Work(sessions, _grace, At(7, 9), At(11, 16), At(12, 15));
        Work(_reset, _alan, At(8, 10));   // not finished in this sprint: carried over
        Comment(At(12, 10), _alan, _reset, "The mail templates took longer than planned; the reset token part is done.");

        var emptyPassword = Bug(At(9, 14), _katherine, "Login form accepts an empty password", "high", _web, _v10, null,
            "1. Open the login form\n2. Type an email, leave the password empty\n3. Press Sign in\n\n**Expected:** a message under the field\n**Actual:** a server error page");
        Plan(At(9, 14.1), emptyPassword, 0);   // added to the running sprint
        Work(emptyPassword, _me, At(10, 9), At(10, 16), At(11, 11));

        CompleteSprint(0, 17);
    }

    // ---- Sprint 2 (days 17–30): the catalogue ---------------------------------------------------------------

    private void Sprint2()
    {
        var list = Story(15, "Product list page with paging", 5, _catalogue, _web, _v10, 8);
        var productApi = Story(15, "Product API: list and details", 5, _catalogue, _api, _v10, 8);
        var cdn = Story(15, "Product images on a CDN", 3, _catalogue, _infra, _v10, 5, "performance");
        var categories = Story(16, "Category navigation", 3, _catalogue, _web, _v10, 6, "ux");
        var menu = Subtask(At(17, 11), _me, categories, "Menu component");
        var breadcrumbs = Subtask(At(17, 11), _me, categories, "Breadcrumbs");

        foreach (var issue in new[] { list, productApi, cdn, categories, _rememberMe })
            Plan(At(17, 9.8), issue, 1);
        StartSprint(1, 17);
        Do(At(16, 15), _ada, new CreateSprint { Id = _sprints[2], Goal = "Customers find products fast" });

        Work(_reset, _alan, At(17, 11), At(19, 15), At(20, 11), resume: true);
        Work(productApi, _grace, At(17, 11), At(20, 16), At(22, 11));
        Work(list, _katherine, At(18, 9), At(22, 15), At(24, 10));
        Work(cdn, _edsger, At(21, 10), At(25, 14), At(27, 11), 0.8);
        Work(categories, _me, At(22, 10), At(27, 15), At(28, 16));
        Work(menu, _me, At(22, 11), done: At(24, 17));
        Work(breadcrumbs, _me, At(25, 10), done: At(26, 15));

        // Taken out of the sprint, and later dropped.
        Do(At(23, 10), _ada, new PlanIssue(_rememberMe.Id, null, null, null));
        Comment(At(23, 10.1), _ada, _rememberMe, "Taking this out of the sprint: the catalogue matters more for 1.0.");
        Do(At(30, 11), _ada, new TransitionIssue(_rememberMe.Id, "done") { Resolution = "wontdo" });
        Comment(At(30, 11.1), _ada, _rememberMe, "Sessions already last 30 days; nobody asked for this. Closing.");

        _spam = Bug(At(19, 16), _alan, "Sign-up confirmation email lands in spam", "medium", _api, _v10, "Northwind Traders",
            "Gmail and Outlook put the confirmation mail in spam. Probably SPF/DKIM on the sending domain.");
        Plan(At(19, 16.1), _spam, 1);
        Work(_spam, _alan, At(23, 11), At(24, 15), At(25, 16));
        Comment(At(25, 16.2), _alan, _spam, "DKIM record added and the From address changed to no-reply@; mail-tester gives 10/10 now.");

        CompleteSprint(1, 31);
        Do(At(31, 9), _ada, b => new EditVersion(_v10, "1.0", b.Versions[_v10].Description, Date(3), Date(31), true, false));
    }

    // ---- Sprint 3 (days 31–44): search ----------------------------------------------------------------------

    private void Sprint3()
    {
        var search = Story(29, "Search box with suggestions", 5, _catalogue, _web, _v11, 9);
        var index = Story(29, "Search index with Postgres full-text", 5, _catalogue, _db, _v11, 8, "performance");
        var details = Story(29, "Product detail page", 5, _catalogue, _web, _v11, 9, "ux");
        _filters = Story(30, "Filter by price and brand", 3, _catalogue, _web, _v11, 6);
        var upgrade = Create(At(30, 10), _edsger, "task", "Upgrade to .NET 10", "Move every project to net10.0 and the new LTS SDK.", null, 2, _infra, _v11, [], null, "tech-debt");

        foreach (var issue in new[] { search, index, details, _filters, upgrade })
            Plan(At(31, 9.8), issue, 2);
        StartSprint(2, 31);
        Do(At(30, 15), _ada, new CreateSprint { Id = _sprints[3], Goal = "Customers can put products in a cart" });

        Work(upgrade, _edsger, At(31, 11), At(33, 15), At(35, 10));
        Work(index, _grace, At(31, 11), At(36, 15), At(38, 11), 1.3);
        Work(search, _katherine, At(32, 9), At(40, 15), At(42, 11));
        // The estimate grows mid-sprint: scope change in the burndown.
        Comment(At(33, 10), _ada, search, "Can the suggestions also show recent searches?");
        Comment(At(33, 11), _katherine, search, "Yes, but that's more work: I've raised it to 8 points.");
        Do(At(33, 11.1), _katherine, new EditIssue(search.Id, IssueFields.StoryPoints, new FieldValue.Number(8)));
        Work(details, _me, At(33, 10), At(39, 15), At(40, 16));
        Work(_filters, _alan, At(38, 10));   // carried over
        Do(At(36, 10), _grace, new AddLink(index.Id, "blocks", search.Id));

        var currency = Bug(At(34, 10), _katherine, "Prices show the wrong currency in some locales", "high", _web, _v11, "Contoso Ltd",
            "With the browser set to de-CH prices show € instead of CHF.");
        Plan(At(34, 10.1), currency, 2);
        Work(currency, _katherine, At(35, 9), At(36, 11), At(36, 16));

        var crash = Bug(At(37, 9), _edsger, "Crash when a product has no image", "highest", _api, _v11, null,
            "Product pages for items without an image return 500. See the attached log.");
        Do(At(37, 9.1), _edsger, new AddAttachment(crash.Id, "crash-log.txt", Encoding.UTF8.GetBytes(CrashLog)));
        Do(At(37, 9.1), _edsger, new EditIssue(crash.Id, IssueFields.Due, new FieldValue.Date(Date(38))));
        Plan(At(37, 9.2), crash, 2);
        Work(crash, _grace, At(37, 9.5), At(37, 14), At(37, 17));
        Comment(At(37, 14.1), _grace, crash, "Root cause: the importer writes `null` image URLs. Fixed the mapper and added a placeholder image, with a regression test.");

        var duplicate = Bug(At(41, 10), _katherine, "Welcome email goes to the junk folder", "medium", _api, null, "Fabrikam",
            "A customer says the welcome mail went to junk.");
        Do(At(41, 10.2), _alan, new AddLink(duplicate.Id, "duplicates", _spam.Id));
        Do(At(41, 10.3), _alan, new TransitionIssue(duplicate.Id, "done") { Resolution = "duplicate" });
        Comment(At(41, 10.3), _alan, duplicate, "Same cause as the spam issue, fixed before this customer signed up. Their mail predates the fix.");

        CompleteSprint(2, 45);
    }

    // ---- Sprint 4 (from day 45, three days ago): the cart, running now --------------------------------------

    private void Sprint4()
    {
        _cartApi = Story(43, "Cart API", 5, _cart, _api, _v11, 9);
        _cartPage = Story(43, "Cart page with quantities", 5, _cart, _web, _v11, 9, "ux");
        _cartSync = Story(43, "Cart follows you across devices", 3, _cart, _api, _v11, 6);
        _addToCart = Story(43, "Add to cart button", 3, _cart, _web, _v11, 10);
        var mini = Story(44, "Mini cart in the header", 2, _cart, _web, _v11, 5, "ux");
        var tax = Create(At(44, 10), _ada, "spike", "Tax rules per country", "Which VAT rules apply when we ship to the EU and Switzerland?", _cart, 2, "", _v20, []);
        var busy = Subtask(At(45, 11), _me, _addToCart, "Busy state while adding");
        var optimistic = Subtask(At(45, 11), _me, _addToCart, "Optimistic update of the counter");
        Subtask(At(45, 11), _me, _addToCart, "Analytics event");

        foreach (var issue in new[] { _cartApi, _cartPage, _cartSync, _addToCart, mini, tax })
            Plan(At(45, 9.8), issue, 3);
        Do(At(45, 9.9), _ada, new EditIssue(mini.Id, IssueFields.Due, new FieldValue.Date(Date(50))));
        StartSprint(3, 45);

        Do(At(45, 10.5), _grace, new AddLink(_cartApi.Id, "blocks", _cartPage.Id));
        Do(At(45, 10.5), _grace, new AddLink(_cartSync.Id, "relates", _cartApi.Id));
        Work(_cartApi, _grace, At(45, 11), At(46, 14), At(47, 11), keepBranch: true);
        Work(_cartPage, _katherine, At(45, 13), At(47, 15));
        Do(At(46, 9), _katherine, new AddAttachment(_cartPage.Id, "cart-wireframe.svg", Encoding.UTF8.GetBytes(Wireframe)));
        Work(_addToCart, _me, At(46, 9));
        Work(busy, _me, At(46, 10), done: At(46, 15));
        Work(optimistic, _me, At(47, 9));
        Comment(At(47, 9.2), _me, _addToCart, "Working on the optimistic update now; the counter jumps before the API answers.");
        Work(_cartSync, _grace, At(47, 13));
        Comment(At(47, 13.1), _grace, _cartSync, "Needs the Cart API merged first (done this morning). Starting.");
        Work(tax, _ada, At(46, 10));
        Work(_filters, _alan, At(45, 11), resume: true);   // still in progress, carried twice
        Comment(At(47, 16), _ada, _filters, "This has slipped two sprints: can we split brand filtering out?");

        // In Progress is over its WIP limit of 4 now.
        _searchFlicker = Bug(At(46, 11), _katherine, "Search suggestions flicker on slow connections", "medium", _web, _v11, null,
            "On a throttled connection (Slow 3G) the suggestion list empties and refills on every key press.");
        Plan(At(46, 11.1), _searchFlicker, 3);
        Work(_searchFlicker, _me, At(47, 10));

        Bug(At(47, 11), _alan, "Product page slow with 200+ reviews", "high", _web, null, "Northwind Traders",
            "Pages with many reviews take ~4 s to render: all reviews are loaded at once.");
        Bug(At(47, 14), _edsger, "Footer links overlap on small phones", "low", _mobile, null, null,
            "Below 360 px the footer columns overlap.");
    }

    // ---- Planned next and the backlog -----------------------------------------------------------------------

    private void Later()
    {
        var address = Story(44, "Checkout: shipping address", 5, _cart, _web, _v20, 9);
        var summary = Story(44, "Checkout: order summary", 3, _cart, _web, _v20, 8);
        var confirmation = Story(44, "Order confirmation email", 3, _orders, _api, _v20, 7);
        var card = Story(44, "Pay by card", 8, _payments, _api, _v20, 10, "security");
        var refunds = Story(44, "Refunds", 5, _payments, _api, _v20, 6);
        Do(At(46, 9), _ada, new CreateSprint { Id = _sprints[4], Goal = "Customers can check out" });
        Do(At(46, 9), _ada, new CreateSprint { Id = _sprints[5], Goal = "Customers can pay" });
        Do(At(46, 9.2), _ada, b => new EditSprint(_sprints[4], b.Sprints[_sprints[4]].Name, b.Sprints[_sprints[4]].Goal, null, null, 22));
        foreach (var issue in new[] { address, summary, confirmation })
            Plan(At(46, 9.5), issue, 4);
        foreach (var issue in new[] { card, refunds })
            Plan(At(46, 9.5), issue, 5);
        Do(At(46, 9.6), _ada, new AddLink(card.Id, "blocks", refunds.Id));
        Do(At(46, 9.6), _ada, new EditIssue(address.Id, IssueFields.Due, new FieldValue.Date(Date(62))));

        Story(40, "Wish list", 5, null, _web, null, 4, "ux");
        var history = Story(40, "Order history page", 5, _orders, _web, _v20, 7);
        // Order history has no dates of its own: the timeline guesses them from these.
        Do(At(46, 9.7), _ada, new BulkEdit([history.Id], [(IssueFields.Start, new FieldValue.Date(Date(66))), (IssueFields.Due, new FieldValue.Date(Date(80)))]));
        Do(At(46, 9.7), _ada, new BulkEdit([confirmation.Id], [(IssueFields.Start, new FieldValue.Date(Date(60))), (IssueFields.Due, new FieldValue.Date(Date(64)))]));
        Story(40, "Reorder from history", 3, _orders, _web, null, 5);
        Story(41, "Product reviews and ratings", 8, _catalogue, _web, null, 6);
        Story(41, "Dark mode", 3, null, _web, null, 2, "ux");
        Story(42, "Mobile app: sign in", 5, _mobileApp, _mobile, null, 5, "mobile");
        Story(42, "Mobile app: product list", 5, _mobileApp, _mobile, null, 5, "mobile");
        var audit = Create(At(42, 10), _ada, "task", "Accessibility audit of the storefront", "Keyboard paths, contrast, screen reader labels (WCAG 2.2 AA).", null, 3, _web, _v11, [], null, "accessibility");
        Do(At(42, 10.1), _ada, new EditIssue(audit.Id, IssueFields.Due, new FieldValue.Date(Date(46))));   // overdue
        Create(At(42, 11), _grace, "task", "Load test the API", "500 concurrent shoppers against the staging API; find the first bottleneck.", null, 3, _api, null, [], null, "performance");
        // A story nobody estimated yet ("Stories without points").
        Create(At(43, 15), _katherine, "story", "Gift cards", "Sell and redeem gift cards. Needs a talk with finance first.", null, null, _web, null, []);
    }

    private const string CrashLog =
        """
        2026-09-14 09:02:11.384 ERR Unhandled exception for GET /api/products/1182
        System.NullReferenceException: Object reference not set to an instance of an object.
           at Shop.Api.Products.ProductMapper.ToDetails(Product product) in ProductMapper.cs:line 41
           at Shop.Api.Products.ProductEndpoints.GetAsync(Int32 id) in ProductEndpoints.cs:line 27
        2026-09-14 09:02:11.390 INF Request finished 500 in 12 ms
        """;

    private const string Wireframe =
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="320" height="200" viewBox="0 0 320 200">
          <rect x="1" y="1" width="318" height="198" fill="#fff" stroke="#888"/>
          <text x="12" y="24" font-family="sans-serif" font-size="14">Your cart</text>
          <rect x="12" y="40" width="296" height="36" fill="none" stroke="#bbb"/>
          <text x="20" y="63" font-family="sans-serif" font-size="12">Blue mug   – 1 +   12.00</text>
          <rect x="12" y="84" width="296" height="36" fill="none" stroke="#bbb"/>
          <text x="20" y="107" font-family="sans-serif" font-size="12">Tea towel  – 2 +   18.00</text>
          <rect x="208" y="150" width="100" height="32" fill="#2b6cb0"/>
          <text x="224" y="171" font-family="sans-serif" font-size="12" fill="#fff">Check out</text>
        </svg>
        """;
}

/// <summary>The demo repository: commits backdated to their step, on branches named after the issues.</summary>
internal sealed class DemoGit
{
    private readonly string _root;
    private readonly string _git;
    private DateTimeOffset _when;
    private string _current = "main";

    private DemoGit(string root, string git)
    {
        _root = root;
        _git = git;
    }

    public int Commits { get; private set; }
    public HashSet<string> Branches { get; } = ["main"];

    public static DemoGit? TryCreate(string root)
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            probe!.WaitForExit();
            return probe.ExitCode == 0 ? new DemoGit(root, "git") : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public DemoGit At(DateTimeOffset when)
    {
        _when = when;
        return this;
    }

    public void Init(Person who)
    {
        Directory.CreateDirectory(_root);
        Run(null, "init", "-b", "main");
        File.WriteAllText(Path.Combine(_root, "README.md"), "# Web Shop\n\nA demo repository for DevHub's Board: branches and commits name the board's issues (SHOP-12).\n");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "# The board lives next to the code but isn't part of it.\nboard/\n");
        Run(null, "add", ".");
        Commit(who, "Initial commit");
    }

    /// <summary>Leaves the working tree on main.</summary>
    public void Finish() => Switch("main");

    public void Branch(string name)
    {
        Switch("main");
        Run(null, "switch", "-c", name);
        _current = name;
        Branches.Add(name);
    }

    public void Commit(string branch, Person who, string message, string area, string topic)
    {
        Switch(branch);
        var file = Path.Combine(_root, "src", area, Slug(topic) + ".md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.AppendAllText(file, $"- {message}\n");
        Run(null, "add", ".");
        Commit(who, message);
    }

    public void Merge(string branch, Person who, string message, bool delete)
    {
        Switch("main");
        Run(who, "merge", "--no-ff", "-m", message, branch);
        Commits++;
        if (delete)
        {
            Run(null, "branch", "-d", branch);
            Branches.Remove(branch);
        }
    }

    private void Commit(Person who, string message)
    {
        Run(who, "commit", "-q", "-m", message);
        Commits++;
    }

    private void Switch(string branch)
    {
        if (_current == branch)
            return;
        Run(null, "switch", "-q", branch);
        _current = branch;
    }

    public static string Slug(string text)
    {
        var slug = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                slug.Append(c);
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }
        var result = slug.ToString().Trim('-');
        return result.Length > 40 ? result[..40].TrimEnd('-') : result;
    }

    private void Run(Person? who, params string[] arguments)
    {
        var start = new ProcessStartInfo(_git)
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        if (who is not null)
        {
            var date = _when.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
            var email = who.Email ?? "you@shop.example";
            start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = who.Name;
            start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = email;
            start.Environment["GIT_AUTHOR_DATE"] = start.Environment["GIT_COMMITTER_DATE"] = date;
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error.Trim()} {output.Result.Trim()}");
    }
}

/// <summary>The demo's shortcut: every acceptance criterion met and the whole Definition of Done ticked, in one change.</summary>
internal sealed record TickAll(string IssueId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var issue = board.Issue(IssueId)!;
        var change = new ChangeBuilder(board, context);
        var dod = board.LevelOf(issue) == IssueTypeLevel.Standard ? board.Project.DefinitionOfDone.Select(d => d.Id).ToList() : [];
        change.WriteIssue(issue, issue with { Criteria = issue.Criteria.Select(c => c with { Done = true }).ToList(), DoneChecks = dod });
        foreach (var c in issue.Criteria.Where(c => !c.Done))
            change.AddHistory(issue.Id, "criteria-met", null, c.Text);
        return change.Build($"{issue.Key}: everything ticked");
    }
}
