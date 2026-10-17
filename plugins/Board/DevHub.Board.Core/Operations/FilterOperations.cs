using DevHub.Board.Core.Model;
using DevHub.Board.Core.Search;
using DevHub.Board.Core.Storage;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Core;

/// <summary>Saves a search (new, or changes the one with <see cref="Id"/>).</summary>
public sealed record SaveFilter(string Name, string Query) : IBoardOperation
{
    public string? Id { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var name = Definitions.RequireName(Name, "filter");
        Definitions.RequireUnique(board.Filters.Values, f => f.Name, name, "filter", f => f.Id == Id);
        try
        {
            QueryCompiler.Compile(Query, new QueryContext(board, null, DateTimeOffset.UnixEpoch));
        }
        catch (QueryException e)
        {
            throw new BoardRuleException(L.T("The query has a problem: ") + e.Message);
        }
        var change = new ChangeBuilder(board, context);
        var existing = Id is null ? null : board.Filters.GetValueOrDefault(Id);
        var filter = new SavedFilter
        {
            Id = existing?.Id ?? Definitions.NewIdFor(change, Id, board.Filters.Keys),
            Name = name,
            Query = Query.Trim(),
            Owner = existing?.Owner ?? context.Who,
        };
        change.Write(BoardPaths.Filter(filter.Id), filter);
        return change.Build(L.T($"Filter {name} saved"));
    }
}

public sealed record DeleteFilter(string FilterId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var filter = board.Filters.GetValueOrDefault(FilterId) ?? throw new BoardRuleException(L.T("That filter doesn't exist any more."));
        var change = new ChangeBuilder(board, context);
        change.Delete(BoardPaths.Filter(filter.Id));
        return change.Build(L.T($"Filter {filter.Name} deleted"));
    }
}
