using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using DevHub.Board.Timeline.Core;
using Xunit;

namespace DevHub.Board.Timeline.Core.Tests;

public sealed class TimelineBoard : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "devhub-timeline-" + Guid.NewGuid().ToString("N"));

    public TimelineBoard()
    {
        Session = BoardSession.CreateAsync(new FolderBoardStore(_folder), new CreateBoard("DEV", "Dev", BoardPreset.Scrum, new BoardUser("Ada")))
            .GetAwaiter().GetResult();
    }

    public BoardSession Session { get; }
    public BoardSnapshot Snapshot => Session.Snapshot;
    public void Apply(IBoardOperation op) => Session.ApplyAsync(op).GetAwaiter().GetResult();

    public string Create(string type, string summary, string? parent = null)
    {
        var id = BoardIds.New();
        Apply(new CreateIssue(type, summary) { Id = id, Parent = parent });
        return id;
    }

    public void Dates(string id, DateOnly? start, DateOnly? due) =>
        Apply(new BulkEdit([id], [(IssueFields.Start, new FieldValue.Date(start)), (IssueFields.Due, new FieldValue.Date(due))]));

    public void Dispose()
    {
        Session.Dispose();
        try { Directory.Delete(_folder, true); } catch { }
    }
}

public class RoadmapTests
{
    private static readonly DateOnly Today = new(2026, 10, 13);

    [Fact]
    public void Epics_run_from_start_to_due_with_progress()
    {
        using var board = new TimelineBoard();
        var epic = board.Create("epic", "Accounts");
        board.Dates(epic, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        var a = board.Create("story", "A", epic);
        board.Create("story", "B", epic);
        board.Apply(new TransitionIssue(a, "done"));

        var result = Roadmap.Build(board.Snapshot, Today);

        var item = Assert.Single(result.Items);
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), false), (item.Start, item.End, item.DatesGuessed));
        Assert.Equal(0.5, item.Progress);
        Assert.False(item.IsLate);
        Assert.Equal(31, item.Days);
        Assert.Equal(new DateOnly(2026, 9, 28), result.From);
        Assert.Equal(new DateOnly(2026, 11, 7), result.To);
        Assert.Equal(3.0 / result.Days, result.Fraction(new DateOnly(2026, 10, 1)), 6);
    }

    [Fact]
    public void Missing_dates_come_from_the_epics_issues_or_a_default()
    {
        using var board = new TimelineBoard();
        var epic = board.Create("epic", "Accounts");
        var a = board.Create("story", "A", epic);
        board.Dates(a, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 20));
        var bare = board.Create("epic", "Later");

        var result = Roadmap.Build(board.Snapshot, Today, withIssues: true);

        var accounts = result.Items.First(i => i.Issue.Id == epic);
        Assert.Equal((new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 20), true), (accounts.Start, accounts.End, accounts.DatesGuessed));
        Assert.Contains(result.Items, i => i.Issue.Id == a && i.Depth == 1);
        var later = result.Items.First(i => i.Issue.Id == bare);
        Assert.Equal(Roadmap.DefaultLengthDays, later.Days);
    }

    [Fact]
    public void Late_epics_and_blockers_show()
    {
        using var board = new TimelineBoard();
        var first = board.Create("epic", "Backend");
        var second = board.Create("epic", "Frontend");
        board.Dates(first, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 20));
        board.Dates(second, new DateOnly(2026, 10, 15), new DateOnly(2026, 11, 15));
        board.Apply(new AddLink(first, "blocks", second));

        var result = Roadmap.Build(board.Snapshot, new DateOnly(2026, 10, 25));

        Assert.True(result.Items.First(i => i.Issue.Id == first).IsLate);
        var blocked = result.Items.First(i => i.Issue.Id == second);
        Assert.Equal(first, Assert.Single(blocked.BlockedBy).Id);
        Assert.True(blocked.HasConflict);   // starts before its blocker ends
    }
}

public class ReleaseTests
{
    [Fact]
    public void Notes_group_done_issues_and_releasing_moves_the_rest()
    {
        using var board = new TimelineBoard();
        board.Apply(new CreateVersion("1.0") { Id = "v1", Release = new DateOnly(2026, 12, 1), Description = "The first one." });
        board.Apply(new CreateVersion("1.1") { Id = "v11" });
        var story = board.Create("story", "Sign in");
        var bug = board.Create("bug", "Crash on start");
        var open = board.Create("task", "Docs");
        board.Apply(new BulkEdit([story, bug, open], [(IssueFields.FixVersions, new FieldValue.Items(["v1"]))]));
        board.Apply(new TransitionIssue(story, "done"));
        board.Apply(new TransitionIssue(bug, "done"));

        var notes = Releases.Notes(board.Snapshot, board.Snapshot.Versions["v1"], includeOpen: true);
        Assert.Contains("## 1.0 (2026-12-01)", notes);
        Assert.Contains("The first one.", notes);
        Assert.Contains("### Story\n- Sign in (DEV-1)", notes.ReplaceLineEndings("\n"));
        Assert.Contains("### Fixed\n- Crash on start (DEV-2)", notes.ReplaceLineEndings("\n"));
        Assert.Contains("### Not done yet\n- Docs (DEV-3)", notes.ReplaceLineEndings("\n"));
        Assert.True(notes.IndexOf("### Story", StringComparison.Ordinal) < notes.IndexOf("### Fixed", StringComparison.Ordinal));

        var status = Releases.All(board.Snapshot, new DateOnly(2026, 12, 5)).First(r => r.Version.Id == "v1");
        Assert.Equal((3, 2, true), (status.Issues.Count, status.Done, status.IsLate));

        board.Apply(new ReleaseVersion("v1", new DateOnly(2026, 12, 2)) { MoveOpenTo = "v11" });
        Assert.True(board.Snapshot.Versions["v1"].Released);
        Assert.Equal(["v11"], board.Snapshot.Issue(open)!.FixVersions);
        Assert.Equal(["v1"], board.Snapshot.Issue(story)!.FixVersions);
        Assert.Throws<BoardRuleException>(() => board.Apply(new ReleaseVersion("v1", new DateOnly(2026, 12, 2))));
    }
}
