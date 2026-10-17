using DevHub.Board.Core.Fields;
using Xunit;

namespace DevHub.Board.Core.Tests;

public class BatchTests
{
    [Fact]
    public async Task A_batch_is_one_change_and_one_undo()
    {
        using var board = TestBoard.Create();
        var a = board.Create("story", "A");
        var b = board.Create("story", "B");

        board.Apply(new Batch(
        [
            new TransitionIssue(a.Id, "inprogress"),
            new TransitionIssue(b.Id, "inprogress"),
            new EditIssue(a.Id, IssueFields.Priority, new FieldValue.Text("high")),
            new AddCriterion(b.Id, "Works offline"),
        ], "2 issues moved"));

        Assert.Equal(("inprogress", "inprogress", "high"), (board.Get(a.Id).Status, board.Get(b.Id).Status, board.Get(a.Id).Priority));
        Assert.Single(board.Get(b.Id).Criteria);
        Assert.Equal("2 issues moved", board.Session.UndoDescription);

        await board.Session.UndoAsync();
        Assert.Equal(("todo", "todo"), (board.Get(a.Id).Status, board.Get(b.Id).Status));
        Assert.Empty(board.Get(b.Id).Criteria);
    }

    [Fact]
    public void One_refusal_refuses_the_whole_batch()
    {
        using var board = TestBoard.Create();
        var a = board.Create("story", "A");

        Assert.Throws<BoardRuleException>(() => board.Apply(new Batch(
            [new TransitionIssue(a.Id, "inprogress"), new EditIssue(a.Id, IssueFields.Priority, new FieldValue.Text("nope"))], "x")));
        Assert.Equal("todo", board.Get(a.Id).Status);
        Assert.Throws<BoardRuleException>(() => board.Apply(new Batch([], "x")));
    }

    [Fact]
    public void Later_steps_see_earlier_ones_for_ranking()
    {
        using var board = TestBoard.Create();
        var a = board.Create("story", "A");
        var b = board.Create("story", "B");
        var c = board.Create("story", "C");

        // C then A to the top, in that order: each placed below the one before.
        board.Apply(new Batch([new RankIssue(c.Id, null, a.Id), new RankIssue(a.Id, c.Id, b.Id)], "ranked"));

        var order = board.Snapshot.Issues.Values.OrderBy(i => i.Rank, StringComparer.Ordinal).Select(i => i.Summary);
        Assert.Equal(["C", "A", "B"], order);
    }
}
