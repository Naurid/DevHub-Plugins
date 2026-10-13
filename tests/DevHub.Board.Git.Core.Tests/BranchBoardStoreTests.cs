using System.Text;
using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Storage;
using DevHub.Board.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Board.Git.Core.Tests;

public class BranchBoardStoreTests
{
    private static BoardChange Change(params (string Path, string? Text)[] files) =>
        new("test", files.ToDictionary(f => f.Path, f => f.Text is null ? null : Encoding.UTF8.GetBytes(f.Text)));

    // A repository with a commit and a local change, so "untouched" can be checked.
    private static TempGitRepo RepoWithWork()
    {
        var repo = new TempGitRepo();
        repo.WriteFile("app.txt", "hello\n");
        repo.Run("add", "app.txt");
        repo.Run("commit", "-m", "Start");
        repo.WriteFile("app.txt", "hello\nedited, not staged\n");
        repo.WriteFile("staged.txt", "staged\n");
        repo.Run("add", "staged.txt");
        return repo;
    }

    private static (string Head, string Status, string Index) State(TempGitRepo repo) =>
        (repo.Run("rev-parse", "HEAD").StandardOutput, repo.Run("status", "--porcelain=v2").StandardOutput,
            repo.Run("ls-files", "--stage").StandardOutput);

    [GitFact]
    public async Task A_repository_without_the_branch_holds_no_board()
    {
        using var repo = RepoWithWork();
        using var store = new BranchBoardStore(repo.Runner, repo.Path);

        Assert.Null(await store.LoadAsync());
    }

    [GitFact]
    public async Task Writes_and_reads_files_on_the_branch_without_touching_the_work()
    {
        using var repo = RepoWithWork();
        var before = State(repo);
        using var store = new BranchBoardStore(repo.Runner, repo.Path);

        var v1 = await store.WriteAsync(Change(("board.json", "{}"), ("issues/a.json", "A")), BoardFiles.NoBoard);
        var v2 = await store.WriteAsync(Change(("issues/b.json", "B"), ("issues/a.json", null)), v1);
        var files = await store.LoadAsync();

        Assert.NotNull(files);
        Assert.Equal(v2, files.Version);
        Assert.Equal(["board.json", "issues/b.json"], files.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("B", Encoding.UTF8.GetString(files.Files["issues/b.json"]));
        Assert.Equal(before, State(repo));
        Assert.Equal(v1, repo.Run("rev-parse", v2 + "^").StandardOutput.Trim());
        Assert.Contains("board: test", repo.Run("log", "-1", "--format=%s", BranchBoardStore.Ref).StandardOutput);
        Assert.Empty(Directory.GetFiles(Path.Combine(repo.Path, ".git", "devhub")));
    }

    [GitFact]
    public async Task Binary_files_survive_the_round_trip()
    {
        using var repo = RepoWithWork();
        using var store = new BranchBoardStore(repo.Runner, repo.Path);
        byte[] bytes = [0, 0xFF, 13, 10, 0x80, 0, 1, 2];

        var v1 = await store.WriteAsync(Change(("board.json", "{}")), BoardFiles.NoBoard);
        await store.WriteAsync(new BoardChange("bin", new Dictionary<string, byte[]?> { ["attachments/x/1-a.bin"] = bytes, ["crlf.txt"] = "a\r\nb\r\n"u8.ToArray() }), v1);

        var files = (await store.LoadAsync())!;
        Assert.Equal(bytes, files.Files["attachments/x/1-a.bin"]);
        Assert.Equal("a\r\nb\r\n"u8.ToArray(), files.Files["crlf.txt"]);
    }

    [GitFact]
    public async Task A_write_based_on_an_old_version_is_refused()
    {
        using var repo = RepoWithWork();
        using var store = new BranchBoardStore(repo.Runner, repo.Path);
        var v1 = await store.WriteAsync(Change(("board.json", "{}")), BoardFiles.NoBoard);
        await store.WriteAsync(Change(("issues/a.json", "mine")), v1);

        await Assert.ThrowsAsync<BoardConflictException>(() => store.WriteAsync(Change(("issues/a.json", "stale")), v1));
        await Assert.ThrowsAsync<BoardConflictException>(() => store.WriteAsync(Change(("board.json", "{}")), BoardFiles.NoBoard));
        Assert.Equal("mine", Encoding.UTF8.GetString((await store.LoadAsync())!.Files["issues/a.json"]));
    }

    [GitFact]
    public async Task A_whole_board_works_on_the_branch()
    {
        using var repo = RepoWithWork();
        var before = State(repo);
        var store = new BranchBoardStore(repo.Runner, repo.Path);
        using var session = await BoardSession.CreateAsync(store, new CreateBoard("DEV", "Repo board", BoardPreset.Scrum, new BoardUser("Ada", "ada@example.com")));
        var id = BoardIdsForTests.New();

        await session.ApplyAsync(new CreateIssue("story", "Login") { Id = id });
        await session.ApplyAsync(new EditIssue(id, IssueFields.StoryPoints, new FieldValue.Number(5)));
        await session.ApplyAsync(new AddAttachment(id, "shot.png", [137, 80, 78, 71, 0, 1]));

        using var again = (await BoardSession.OpenAsync(new BranchBoardStore(repo.Runner, repo.Path), new BoardUser("Ada", "ada@example.com")))!;
        var issue = again.Snapshot.Issue(id)!;
        Assert.Equal(5, issue.StoryPoints);
        Assert.Equal([137, 80, 78, 71, 0, 1], again.Snapshot.AttachmentBytes(issue, issue.Attachments.Single()));
        Assert.Equal(before, State(repo));
    }

    [GitFact]
    public async Task A_repository_without_a_user_name_still_gets_commits()
    {
        using var repo = new TempGitRepo();   // its config has no user.name; TempGitRepo's environment gives the author
        using var store = new BranchBoardStore(repo.Runner, repo.Path);

        var version = await store.WriteAsync(Change(("board.json", "{}")), BoardFiles.NoBoard);

        Assert.Equal(40, version.Length);
    }

    [GitFact]
    public async Task Another_store_writing_the_branch_is_noticed()
    {
        using var repo = RepoWithWork();
        using var mine = new BranchBoardStore(repo.Runner, repo.Path);
        using var theirs = new BranchBoardStore(repo.Runner, repo.Path);
        var v1 = await mine.WriteAsync(Change(("board.json", "{}")), BoardFiles.NoBoard);
        await mine.LoadAsync();
        var noticed = new TaskCompletionSource();
        mine.ChangedOutside += (_, _) => noticed.TrySetResult();
        await Task.Delay(1500);   // the watcher starts in the background

        await theirs.WriteAsync(Change(("issues/a.json", "A")), v1);

        Assert.Same(noticed.Task, await Task.WhenAny(noticed.Task, Task.Delay(TimeSpan.FromSeconds(10))));
    }

    [GitFact]
    public async Task Only_repositories_can_hold_a_branch_board()
    {
        using var repo = RepoWithWork();
        var provider = new BranchBoardStoreProvider(repo.Runner);

        Assert.True(await provider.CanUseAsync(repo.Path));
        Assert.False(await provider.CanUseAsync(Path.GetTempPath()));
        Assert.False(await provider.CanUseAsync(Path.Combine(repo.Path, "missing")));
    }
}

internal static class BoardIdsForTests
{
    public static string New() => DevHub.Board.Core.Model.BoardIds.New();
}
