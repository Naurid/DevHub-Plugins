using DevHub.Board.Core;
using DevHub.Board.Core.Storage;
using DevHub.Board.Risks.Core;
using Xunit;

namespace DevHub.Board.Risks.Core.Tests;

public sealed class RiskBoard : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "devhub-risks-" + Guid.NewGuid().ToString("N"));

    public RiskBoard()
    {
        Session = BoardSession.CreateAsync(new FolderBoardStore(_folder), new CreateBoard("DEV", "Dev", BoardPreset.Scrum, new BoardUser("Ada")))
            .GetAwaiter().GetResult();
    }

    public BoardSession Session { get; }
    public BoardSnapshot Snapshot => Session.Snapshot;

    public void Apply(IBoardOperation op) => Session.ApplyAsync(op).GetAwaiter().GetResult();

    public void Dispose()
    {
        Session.Dispose();
        try { Directory.Delete(_folder, true); } catch { }
    }
}

public class RiskTests
{
    [Fact]
    public void Risks_are_numbered_scored_and_sorted()
    {
        using var board = new RiskBoard();
        board.Apply(new AddRisk("Team doesn't know the stack") { Id = "r1", Probability = 4, Impact = 3, Category = "Team" });
        board.Apply(new AddRisk("Payment provider changes its API") { Id = "r2", Probability = 2, Impact = 5 });
        board.Apply(new AddRisk("  Key person leaves  ") { Id = "r3", Probability = 2, Impact = 2 });

        var all = RiskRegister.All(board.Snapshot);
        Assert.Equal(["R-1", "R-2", "R-3"], all.Select(r => r.Key));   // 12, 10, 4
        Assert.Equal(("High", "High", "Low"), (all[0].Level, all[1].Level, all[2].Level));
        Assert.Equal("Key person leaves", all[2].Title);

        Assert.Throws<BoardRuleException>(() => board.Apply(new AddRisk(" ")));
        Assert.Throws<BoardRuleException>(() => board.Apply(new AddRisk("x") { Probability = 6 }));
        Assert.Throws<BoardRuleException>(() => board.Apply(new AddRisk("x") { Owner = "nobody" }));
    }

    [Fact]
    public void A_risk_is_edited_closed_and_deleted()
    {
        using var board = new RiskBoard();
        board.Apply(new AddRisk("Late hardware") { Id = "r1" });
        var risk = RiskRegister.Get(board.Snapshot, "r1")!;

        board.Apply(new EditRisk(risk with { Impact = 5, Mitigation = " Order early ", Status = RiskStatus.Mitigating, Number = 99 }));
        var edited = RiskRegister.Get(board.Snapshot, "r1")!;
        Assert.Equal((5, "Order early", RiskStatus.Mitigating, 1), (edited.Impact, edited.Mitigation, edited.Status, edited.Number));

        board.Apply(new DeleteRisk("r1"));
        Assert.Empty(RiskRegister.All(board.Snapshot));
    }

    [Fact]
    public void The_matrix_puts_open_risks_in_their_cells()
    {
        using var board = new RiskBoard();
        board.Apply(new AddRisk("A") { Id = "a", Probability = 5, Impact = 1 });
        board.Apply(new AddRisk("B") { Id = "b", Probability = 5, Impact = 1 });
        board.Apply(new AddRisk("C") { Id = "c", Probability = 1, Impact = 5 });
        board.Apply(new EditRisk(RiskRegister.Get(board.Snapshot, "c")! with { Status = RiskStatus.Closed }));

        var matrix = RiskRegister.Matrix(RiskRegister.All(board.Snapshot));

        Assert.Equal(5, matrix.Series.Count);
        Assert.Equal("5 · Very high", matrix.Series[0].Name);          // probability 5 on top
        Assert.Equal(25, matrix.Series[0].Values[4]);                 // its score
        Assert.Equal("R-1 R-2", matrix.CellText![0][0]);              // probability 5, impact 1
        Assert.Null(matrix.CellText[4][4]);                           // C is closed: not shown

        var table = RiskRegister.Table(board.Snapshot, RiskRegister.All(board.Snapshot));
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("Closed", table.Rows.Single(r => r[1] == "C")[7]);
    }
}
