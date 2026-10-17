using System.Text;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;

namespace DevHub.Board.Core;

/// <summary>Reading retrospectives (<c>retros/</c>) and writing them up as Markdown.</summary>
public static class Retrospectives
{
    public const string Folder = "retros/";

    public static string PathOf(string sprintId) => Folder + sprintId + ".json";

    /// <summary>A sprint's retrospective, or an empty one when it has none yet.</summary>
    public static Retrospective Of(BoardSnapshot board, string sprintId) =>
        board.Files.TryGetValue(PathOf(sprintId), out var bytes) && TryRead(bytes) is { } retro
            ? retro with { Sprint = sprintId }
            : new Retrospective { Sprint = sprintId };

    /// <summary>Every retrospective there is, in sprint order.</summary>
    public static IReadOnlyList<Retrospective> All(BoardSnapshot board) =>
        board.FilesIn(Folder)
            .Select(f => (Id: BoardPaths.IdOf(f.Key, Folder, ".json"), Retro: TryRead(f.Value)))
            .Where(r => r.Id is not null && r.Retro is not null && board.Sprints.ContainsKey(r.Id))
            .Select(r => r.Retro! with { Sprint = r.Id! })
            .OrderBy(r => Order(board.Sprints[r.Sprint]))
            .ToList();

    /// <summary>Actions from retrospectives of sprints before <paramref name="sprintId"/> that aren't done: to follow up.</summary>
    public static IReadOnlyList<(Retrospective Retro, RetroAction Action)> OpenActionsBefore(BoardSnapshot board, string sprintId)
    {
        if (board.Sprints.GetValueOrDefault(sprintId) is not { } target)
            return [];
        var key = Order(target);
        return All(board)
            .Where(r => r.Sprint != sprintId && Order(board.Sprints[r.Sprint]).CompareTo(key) < 0)
            .SelectMany(r => r.Actions.Where(a => !a.Done).Select(a => (r, a)))
            .ToList();
    }

    // Sprints in time: by start date, then by planning order (future sprints have no dates yet).
    private static (DateTimeOffset, int) Order(Sprint sprint) => (sprint.Start ?? DateTimeOffset.MaxValue, sprint.Sequence);

    /// <summary>The retrospective as Markdown, for a report.</summary>
    public static string ToMarkdown(BoardSnapshot board, Retrospective retro)
    {
        var sprint = board.Sprints.GetValueOrDefault(retro.Sprint);
        var text = new StringBuilder();
        text.Append("# Retrospective: ").AppendLine(sprint?.Name ?? retro.Sprint);
        if (sprint?.Goal is { Length: > 0 } goal)
            text.Append("Sprint goal: ").AppendLine(goal);
        if (sprint is { State: SprintState.Closed })
            text.AppendLine($"Committed {sprint.CommittedPoints:0.##} points, completed {sprint.CompletedPoints:0.##}.");
        void Notes(string title, IReadOnlyList<RetroNote> notes)
        {
            text.AppendLine().Append("## ").AppendLine(title);
            if (notes.Count == 0)
                text.AppendLine("_Nothing noted._");
            foreach (var note in notes)
                text.Append("- ").AppendLine(note.Text);
        }
        Notes("What went well", retro.WentWell);
        Notes("What to improve", retro.ToImprove);
        Notes("Problems", retro.Problems);
        text.AppendLine().AppendLine("## Improvement actions");
        if (retro.Actions.Count == 0)
            text.AppendLine("_None decided._");
        foreach (var action in retro.Actions)
        {
            text.Append(action.Done ? "- [x] " : "- [ ] ").Append(action.Text);
            if (board.Project.Person(action.Owner) is { } owner)
                text.Append(" (").Append(owner.Name).Append(')');
            if (board.Issue(action.Issue) is { } issue)
                text.Append(" → ").Append(issue.Key);
            text.AppendLine();
        }
        text.AppendLine().AppendLine("## Follow-up").AppendLine(retro.FollowUp.Trim().Length > 0 ? retro.FollowUp.Trim() : "_Not written yet._");
        return text.ToString();
    }

    private static Retrospective? TryRead(byte[] bytes)
    {
        try
        {
            return BoardJson.Deserialize<Retrospective>(bytes);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Saves a sprint's retrospective as a whole (the page edits a copy and saves it): empty notes are dropped, new notes and
/// actions get ids, owners and issues must exist.
/// </summary>
public sealed record SaveRetrospective(Retrospective Retro) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var sprint = board.Sprints.GetValueOrDefault(Retro.Sprint) ?? throw new BoardRuleException("The sprint doesn't exist any more.");
        var change = new ChangeBuilder(board, context);
        IReadOnlyList<RetroNote> Clean(IReadOnlyList<RetroNote> notes) => notes
            .Where(n => !string.IsNullOrWhiteSpace(n.Text))
            .Select(n => n with { Id = string.IsNullOrEmpty(n.Id) ? change.NewId() : n.Id, Text = n.Text.Trim() })
            .ToList();
        var actions = Retro.Actions.Where(a => !string.IsNullOrWhiteSpace(a.Text)).Select(a =>
        {
            if (a.Owner is not null && board.Project.Person(a.Owner) is null)
                throw new BoardRuleException($"The owner of \"{a.Text.Trim()}\" isn't on the board.");
            return a with
            {
                Id = string.IsNullOrEmpty(a.Id) ? change.NewId() : a.Id,
                Text = a.Text.Trim(),
                Issue = board.Issue(a.Issue)?.Id,
            };
        }).ToList();
        change.Write(Retrospectives.PathOf(sprint.Id), Retro with
        {
            Sprint = sprint.Id,
            WentWell = Clean(Retro.WentWell),
            ToImprove = Clean(Retro.ToImprove),
            Problems = Clean(Retro.Problems),
            Actions = actions,
            FollowUp = Retro.FollowUp.Trim(),
            Updated = context.Now,
        });
        return change.Build($"Retrospective of {sprint.Name} saved");
    }
}

/// <summary>Ticks an action off (or not) in the retrospective of <see cref="SprintId"/>, e.g. from a later sprint's follow-up.</summary>
public sealed record SetRetroActionDone(string SprintId, string ActionId, bool Done) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var retro = Retrospectives.Of(board, SprintId);
        var action = retro.Actions.FirstOrDefault(a => a.Id == ActionId) ?? throw new BoardRuleException("That action doesn't exist any more.");
        var change = new ChangeBuilder(board, context);
        change.Write(Retrospectives.PathOf(SprintId), retro with
        {
            Actions = retro.Actions.Select(a => a.Id == ActionId ? a with { Done = Done } : a).ToList(),
            Updated = context.Now,
        });
        return change.Build($"\"{action.Text}\" {(Done ? "done" : "not done")}");
    }
}
