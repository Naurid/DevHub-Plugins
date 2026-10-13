using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using DevHub.Board.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Board.Git.Core.Tests;

/// <summary>A small board in a temp folder: DEV-1 story "Login form", DEV-2 bug "Crash on start".</summary>
public sealed class SampleBoard : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "devhub-boardgit-" + Guid.NewGuid().ToString("N"));

    public SampleBoard()
    {
        Session = BoardSession.CreateAsync(new FolderBoardStore(_folder), new CreateBoard("DEV", "Dev", BoardPreset.Scrum, new BoardUser("Ada")))
            .GetAwaiter().GetResult();
        Story = Create("story", "Login form");
        Bug = Create("bug", "Crash on start");
    }

    public BoardSession Session { get; }
    public Issue Story { get; private set; }
    public Issue Bug { get; }
    public BoardSnapshot Snapshot => Session.Snapshot;

    private Issue Create(string type, string summary)
    {
        var id = BoardIds.New();
        Session.ApplyAsync(new CreateIssue(type, summary) { Id = id }).GetAwaiter().GetResult();
        return Session.Snapshot.Issue(id)!;
    }

    public void Dispose()
    {
        Session.Dispose();
        try { Directory.Delete(_folder, true); } catch { }
    }
}

public class IssueKeyTests
{
    [Theory]
    [InlineData("feature/DEV-12-login", "DEV-12")]
    [InlineData("feature/dev-12-login", "DEV-12")]
    [InlineData("DEV-12: fix the form", "DEV-12")]
    [InlineData("Fixes DEV-3 and DEV-12, see DEV-3", "DEV-3,DEV-12")]
    [InlineData("xDEV-12 DEV-12a", "")]
    [InlineData("OTHER-5 only", "")]
    [InlineData("DEV-007", "DEV-7")]
    public void Finds_the_projects_keys(string text, string keys) =>
        Assert.Equal(keys.Length == 0 ? [] : keys.Split(','), IssueKeys.Find(text, "DEV"));

    [Fact]
    public void Maps_keys_to_issues_and_old_keys_count()
    {
        using var board = new SampleBoard();
        board.Session.ApplyAsync(new ChangeProjectKey("APP")).GetAwaiter().GetResult();

        var found = IssueKeys.Issues("Fixes DEV-1 (now APP-1) and APP-2", board.Snapshot);

        Assert.Equal([board.Story.Id, board.Bug.Id], found.Select(i => i.Id));
    }
}

public class BranchNameTests
{
    [Fact]
    public void Branches_are_named_after_the_issue()
    {
        using var board = new SampleBoard();

        Assert.Equal("feature/DEV-1-login-form", BranchNames.For(board.Story, board.Snapshot.Project));
        Assert.Equal("bugfix/DEV-2-crash-on-start", BranchNames.For(board.Bug, board.Snapshot.Project));
    }

    [Theory]
    [InlineData("Login form: e-mail & password!", "login-form-e-mail-password")]
    [InlineData("Café über naïve", "cafe-uber-naive")]
    [InlineData("   ---   ", "")]
    [InlineData("A very long summary that goes on and on beyond forty characters", "a-very-long-summary-that-goes-on-and-on")]
    public void Slugs_are_ascii_and_short(string text, string slug) => Assert.Equal(slug, BranchNames.Slug(text));
}

public class SmartCommitTests
{
    [Fact]
    public void Commands_are_read_for_the_named_issue()
    {
        using var board = new SampleBoard();

        var commands = SmartCommits.Parse("DEV-1 #in-progress #comment Started on the layout #time 1h 30m", board.Snapshot);

        Assert.Collection(commands,
            c => Assert.Equal("inprogress", Assert.IsType<SmartCommand.Transition>(c).Status.Id),
            c => Assert.Equal("Started on the layout", Assert.IsType<SmartCommand.Comment>(c).Text),
            c => Assert.Equal(90, Assert.IsType<SmartCommand.Time>(c).Minutes));
        Assert.Equal("DEV-1: move to In Progress", commands[0].Describe(board.Snapshot.Project.Settings));
    }

    [Fact]
    public void Done_picks_a_done_status_and_applies()
    {
        using var board = new SampleBoard();
        var commands = SmartCommits.Parse("Fix the crash DEV-2 #done", board.Snapshot);

        foreach (var command in commands)
            board.Session.ApplyAsync(command.ToOperation(DateTimeOffset.Now)).GetAwaiter().GetResult();

        Assert.Equal("done", board.Snapshot.Issue(board.Bug.Id)!.Status);
    }

    [Theory]
    [InlineData("No issue here #done")]
    [InlineData("DEV-1 no commands")]
    [InlineData("DEV-1 #todo")]          // already To Do
    [InlineData("DEV-1 #nonsense")]
    [InlineData("DEV-99 #done")]         // no such issue
    public void Nothing_without_an_issue_and_a_meaningful_command(string message)
    {
        using var board = new SampleBoard();
        Assert.Empty(SmartCommits.Parse(message, board.Snapshot));
    }
}

public class DevelopmentQueryTests
{
    [GitFact]
    public async Task Finds_the_branches_and_commits_of_an_issue_but_not_the_boards_own()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1");
        repo.Run("add", "a.txt");
        repo.Run("commit", "-m", "Start");
        repo.Run("checkout", "-b", "feature/DEV-12-login");
        repo.WriteFile("a.txt", "2");
        repo.Run("commit", "-am", "DEV-12: form layout");
        repo.WriteFile("a.txt", "3");
        repo.Run("commit", "-am", "Unrelated, mentions DEV-120");
        // The board's own branch names issues in its messages too: left out.
        using (var store = new BranchBoardStore(repo.Runner, repo.Path))
            await store.WriteAsync(new BoardChange("DEV-12: created", new Dictionary<string, byte[]?> { ["board.json"] = "{}"u8.ToArray() }), BoardFiles.NoBoard);
        var queries = new DevelopmentQueries(repo.Runner);

        var info = await queries.ForIssueAsync(repo.Path, "DEV", "DEV-12");

        Assert.Equal("feature/DEV-12-login", Assert.Single(info.Branches).Name);
        Assert.Equal("DEV-12: form layout", Assert.Single(info.Commits).Subject);
        Assert.Equal(2, (await queries.ProjectCommitsAsync(repo.Path, "DEV")).Count);
        Assert.Equal(Path.GetFullPath(repo.Path), await queries.RepositoryOfAsync(Path.Combine(repo.Path)));
    }
}
