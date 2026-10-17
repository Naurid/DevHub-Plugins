using DevHub.Board.Core.Storage;

namespace DevHub.Board.Core;

/// <summary>
/// Several operations as one change (moving or editing a selection of issues): each sees the ones before it, the files
/// they write are merged, and one Undo takes them all back. If one is refused, none is applied.
/// </summary>
public sealed record Batch(IReadOnlyList<IBoardOperation> Operations, string Message) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        if (Operations.Count == 0)
            throw new BoardRuleException("Nothing to change.");
        var files = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        var current = board;
        foreach (var operation in Operations)
        {
            var step = operation.Apply(current, context);
            foreach (var (path, bytes) in step.Files)
                files[path] = bytes;
            current = current.Apply(step, board.Version);
        }
        return new BoardChange(files.Count == 0 ? "No change" : Message, files);
    }
}
