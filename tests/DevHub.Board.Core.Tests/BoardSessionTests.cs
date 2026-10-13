using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Storage;
using Xunit;

namespace DevHub.Board.Core.Tests;

public class BoardSessionTests
{
    [Fact]
    public async Task Two_windows_on_one_board_never_overwrite_each_other()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");
        using var other = (await BoardSession.OpenAsync(new FolderBoardStore(board.Folder.Path), TestBoard.Ada))!;

        // The other window changes the issue; this window's snapshot is now stale.
        await other.ApplyAsync(new EditIssue(issue.Id, IssueFields.StoryPoints, new FieldValue.Number(8)));
        await board.Session.ApplyAsync(new EditIssue(issue.Id, IssueFields.Priority, new FieldValue.Text("high")));

        var result = board.Reload().Issue(issue.Id)!;
        Assert.Equal(8, result.StoryPoints);
        Assert.Equal("high", result.Priority);
    }

    [Fact]
    public async Task A_rule_checked_again_after_a_conflict_can_refuse()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");
        using var other = (await BoardSession.OpenAsync(new FolderBoardStore(board.Folder.Path), TestBoard.Ada))!;

        await other.ApplyAsync(new DeleteIssue(issue.Id));

        await Assert.ThrowsAsync<BoardRuleException>(() =>
            board.Session.ApplyAsync(new EditIssue(issue.Id, IssueFields.Summary, new FieldValue.Text("Sign in"))));
        Assert.Null(board.Snapshot.Issue(issue.Id));
    }

    [Fact]
    public async Task Changed_is_raised_for_own_changes_and_reloads()
    {
        using var board = TestBoard.Create();
        var events = new List<BoardChangedEventArgs>();
        board.Session.Changed += (_, e) => events.Add(e);

        var issue = board.Create("story", "Login");
        using var other = (await BoardSession.OpenAsync(new FolderBoardStore(board.Folder.Path), TestBoard.Ada))!;
        await other.ApplyAsync(new EditIssue(issue.Id, IssueFields.StoryPoints, new FieldValue.Number(3)));
        await board.Session.ReloadAsync();

        Assert.NotNull(events[0].Change);
        Assert.Null(events[^1].Change);
        Assert.Equal(3, events[^1].Snapshot.Issue(issue.Id)!.StoryPoints);
    }

    [Fact]
    public async Task Someone_new_is_added_to_the_board_on_their_first_change()
    {
        using var board = TestBoard.Create();
        var grace = new BoardUser("Grace Hopper", "grace@example.com");
        using var theirs = (await BoardSession.OpenAsync(new FolderBoardStore(board.Folder.Path), grace))!;
        Assert.Null(theirs.CurrentPersonId);

        await theirs.ApplyAsync(new CreateIssue("bug", "Moth in relay") { Id = "b1" });

        Assert.NotNull(theirs.CurrentPersonId);
        Assert.Equal(2, theirs.Snapshot.Project.People.Count);
        Assert.Equal(theirs.CurrentPersonId, theirs.Snapshot.Issue("b1")!.Reporter);
    }

    [Fact]
    public async Task Undo_writes_the_files_back()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");
        await board.Session.ApplyAsync(new EditIssue(issue.Id, IssueFields.StoryPoints, new FieldValue.Number(5)));
        Assert.Equal("DEV-1: story points changed", board.Session.UndoDescription);

        await board.Session.UndoAsync();

        Assert.Null(board.Get(issue.Id).StoryPoints);
        Assert.Null(board.Reload().Issue(issue.Id)!.StoryPoints);
        Assert.Single(board.Snapshot.HistoryOf(issue.Id));   // just "created"

        await board.Session.UndoAsync();                     // the creation
        Assert.Empty(board.Snapshot.Issues);
        Assert.Equal(1, board.Snapshot.Project.NextNumber);
    }

    [Fact]
    public async Task Undo_refuses_when_the_board_changed_since()
    {
        using var board = TestBoard.Create();
        var issue = board.Create("story", "Login");
        await board.Session.ApplyAsync(new EditIssue(issue.Id, IssueFields.StoryPoints, new FieldValue.Number(5)));
        using var other = (await BoardSession.OpenAsync(new FolderBoardStore(board.Folder.Path), TestBoard.Ada))!;
        await other.ApplyAsync(new EditIssue(issue.Id, IssueFields.StoryPoints, new FieldValue.Number(8)));
        await board.Session.ReloadAsync();

        await Assert.ThrowsAsync<BoardRuleException>(() => board.Session.UndoAsync());
        Assert.Equal(8, board.Get(issue.Id).StoryPoints);
    }

    [Fact]
    public async Task Opening_an_empty_store_gives_nothing()
    {
        using var folder = new TempFolder();

        Assert.Null(await BoardSession.OpenAsync(new FolderBoardStore(folder.Path), TestBoard.Ada));
    }
}
