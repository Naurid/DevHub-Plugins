using System.Text.RegularExpressions;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Core;

/// <summary>Open task lines in a Markdown description (<c>- [ ] Write the docs</c>), which Create Issue turns into an epic's issues.</summary>
public static partial class TaskList
{
    // "- [ ] x", "* [ ] x", "-[] x"; a checked box ("- [x]") is left alone.
    [GeneratedRegex(@"^\s*[-*+]\s*\[\s?\]\s*(?<text>\S.*?)\s*$")]
    private static partial Regex OpenTask();

    /// <summary>The text of each open task line, in order.</summary>
    public static IReadOnlyList<string> Items(string? description) =>
        Lines(description).Select(l => OpenTask().Match(l)).Where(m => m.Success).Select(m => m.Groups["text"].Value).ToList();

    /// <summary>The description without its open task lines (and without the blank lines that leaves at either end).</summary>
    public static string Without(string? description) =>
        string.Join("\n", Lines(description).Where(l => !OpenTask().IsMatch(l))).Trim('\n', '\r', ' ', '\t');

    private static string[] Lines(string? text) => (text ?? "").Replace("\r\n", "\n").Split('\n');
}

/// <summary>What a new issue in an epic starts with: the epic's start and due dates and its labels.</summary>
public static class EpicDefaults
{
    /// <summary>
    /// The fields to set on a new issue whose parent is <paramref name="parentId"/>, when that is an epic; only
    /// what the epic has. Empty for no parent, a story parent (sub-tasks) or an unknown id.
    /// </summary>
    public static IReadOnlyList<(string Field, FieldValue Value)> For(BoardSnapshot board, string? parentId)
    {
        if (board.Issue(parentId) is not { } epic || board.LevelOf(epic) != IssueTypeLevel.Epic)
            return [];
        var fields = new List<(string, FieldValue)>();
        if (epic.Start is { } start)
            fields.Add((IssueFields.Start, new FieldValue.Date(start)));
        if (epic.Due is { } due)
            fields.Add((IssueFields.Due, new FieldValue.Date(due)));
        if (epic.Labels.Count > 0)
            fields.Add((IssueFields.Labels, new FieldValue.Items(epic.Labels)));
        return fields;
    }

    /// <summary><paramref name="fields"/> plus the epic's defaults for the fields it doesn't set itself.</summary>
    public static IReadOnlyList<(string Field, FieldValue Value)> Add(BoardSnapshot board, string? parentId,
        IReadOnlyList<(string Field, FieldValue Value)> fields) =>
        [.. fields, .. For(board, parentId).Where(d => fields.All(f => f.Field != d.Field))];
}

/// <summary>
/// Creates an issue and issues under it as one change, so one Undo takes them all back (an epic with the tasks
/// listed in Create Issue). Each child gets the new issue as its parent; children are ranked in the given order.
/// </summary>
public sealed record CreateIssueWithChildren(CreateIssue Parent, IReadOnlyList<CreateIssue> Children) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var parentId = Parent.Id ?? new ChangeBuilder(board, context).NewId();
        var change = (Parent with { Id = parentId }).Apply(board, context);
        if (Children.Count == 0)
            return change;

        // Each step sees the ones before it (the parent, earlier children, the project's next key number);
        // later writes of the same file (project.json, a change log) replace earlier ones.
        var files = new Dictionary<string, byte[]?>(change.Files, StringComparer.Ordinal);
        var current = board.Apply(change, board.Version);
        foreach (var child in Children)
        {
            var step = (child with { Parent = parentId }).Apply(current, context);
            foreach (var (path, bytes) in step.Files)
                files[path] = bytes;
            current = current.Apply(step, board.Version);
        }
        var parent = current.Issue(parentId)!;
        return new BoardChange(L.T($"{parent.Key}: created \"{parent.Summary}\" with {Children.Count} issue(s)"), files);
    }
}
