using DevHub.Board.Core;
using DevHub.Board.Core.Storage;
using DevHub.Board.Git.Core;
using DevHub.Git.Core;
using DevHub.Integration;
using DevHub.Sdk.Core.Settings;
using DevHub.Services.Board;

namespace DevHub.Services.BoardGit;

/// <summary>
/// What Board.Git's parts share: the git runner and queries, which repository a board's issues are developed in,
/// which boards belong to a repository, and short-lived access to a board's snapshot.
/// </summary>
public sealed class BoardGitServices(
    GitRunner runner,
    DevelopmentQueries queries,
    BoardService boards,
    ISettingsStore settings,
    IStatusService status,
    IDialogService dialogs,
    IEnumerable<IGitHubApi> gitHub)
{
    public GitRunner Runner { get; } = runner;
    public DevelopmentQueries Queries { get; } = queries;
    public BoardService Boards { get; } = boards;
    public IStatusService Status { get; } = status;
    public IDialogService Dialogs { get; } = dialogs;
    public IGitHubApi? GitHub { get; } = gitHub.FirstOrDefault();

    public BoardGitSettings Settings => settings.LoadSection<BoardGitSettings>();

    /// <summary>
    /// The repository a board's issues are developed in: the repository of a branch board, the one chosen for the
    /// board (Link Repository…), else the repository its folder is in, else null.
    /// </summary>
    public async Task<string?> RepositoryForAsync(BoardLocation location, CancellationToken ct = default)
    {
        if (Settings.Repositories.TryGetValue(location.Key, out var chosen) && Directory.Exists(chosen))
            return chosen;
        return await Queries.RepositoryOfAsync(location.Location, ct);
    }

    public void LinkRepository(BoardLocation location, string? repository) =>
        settings.UpdateSection<BoardGitSettings>(s =>
        {
            if (repository is null)
                s.Repositories.Remove(location.Key);
            else
                s.Repositories[location.Key] = repository;
        });

    /// <summary>The recent boards whose issues are developed in <paramref name="repository"/>.</summary>
    public async Task<IReadOnlyList<RecentBoard>> BoardsOfRepositoryAsync(string repository, CancellationToken ct = default)
    {
        var top = Path.GetFullPath(repository).TrimEnd('\\', '/');
        var result = new List<RecentBoard>();
        foreach (var recent in Boards.RecentBoards)
        {
            if (Boards.Provider(recent.StoreKind) is null)
                continue;
            var repo = await RepositoryForAsync(new BoardLocation(recent.StoreKind, recent.Location), ct);
            if (repo is not null && string.Equals(Path.GetFullPath(repo).TrimEnd('\\', '/'), top, StringComparison.OrdinalIgnoreCase))
                result.Add(recent);
        }
        return result;
    }

    /// <summary>Runs <paramref name="action"/> with the board at <paramref name="recent"/> open (shared with any open tab).</summary>
    public async Task<T?> WithBoardAsync<T>(RecentBoard recent, Func<BoardContext, Task<T>> action)
    {
        BoardContext? context = null;
        try
        {
            context = await Boards.AcquireAsync(new BoardLocation(recent.StoreKind, recent.Location));
            return context is null ? default : await action(context);
        }
        catch (Exception e) when (e is BoardStoreException or IOException or UnauthorizedAccessException)
        {
            return default;
        }
        finally
        {
            if (context is not null)
                Boards.Release(context);
        }
    }

    /// <summary>The repository's current branch, or null (detached, not a repository).</summary>
    public async Task<string?> CurrentBranchAsync(string repository, CancellationToken ct = default)
    {
        var result = await Runner.RunAsync(repository, ["branch", "--show-current"], new GitRunOptions { ReadOnly = true, AllowFailure = true }, ct);
        var branch = result.StandardOutput.Trim();
        return result.Succeeded && branch.Length > 0 ? branch : null;
    }
}
