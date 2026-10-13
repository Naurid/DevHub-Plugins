using System.Text;
using DevHub.Board.Core.Storage;
using Xunit;

namespace DevHub.Board.Core.Tests;

public class FolderStoreTests
{
    private static BoardChange Change(params (string Path, string? Text)[] files) =>
        new("test", files.ToDictionary(f => f.Path, f => f.Text is null ? null : Encoding.UTF8.GetBytes(f.Text)));

    [Fact]
    public async Task An_empty_folder_holds_no_board()
    {
        using var folder = new TempFolder();
        using var store = new FolderBoardStore(folder.Path);

        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Writes_and_reads_files_with_a_growing_version()
    {
        using var folder = new TempFolder();
        using var store = new FolderBoardStore(folder.Path);

        var v1 = await store.WriteAsync(Change(("board.json", "{}"), ("issues/a.json", "A")), BoardFiles.NoBoard);
        var v2 = await store.WriteAsync(Change(("issues/b.json", "B")), v1);
        var files = await store.LoadAsync();

        Assert.NotNull(files);
        Assert.Equal(v2, files.Version);
        Assert.NotEqual(v1, v2);
        Assert.Equal(["board.json", "issues/a.json", "issues/b.json"], files.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("B", Encoding.UTF8.GetString(files.Files["issues/b.json"]));
        Assert.DoesNotContain(Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories), f => f.EndsWith(".devhub-tmp"));
        Assert.False(File.Exists(Path.Combine(folder.Path, FolderBoardStore.LockFile)));
    }

    [Fact]
    public async Task A_write_based_on_an_old_version_is_refused()
    {
        using var folder = new TempFolder();
        using var store = new FolderBoardStore(folder.Path);
        var v1 = await store.WriteAsync(Change(("board.json", "{}")), BoardFiles.NoBoard);
        await store.WriteAsync(Change(("issues/a.json", "mine")), v1);

        await Assert.ThrowsAsync<BoardConflictException>(() => store.WriteAsync(Change(("issues/a.json", "stale")), v1));
        Assert.Equal("mine", Encoding.UTF8.GetString((await store.LoadAsync())!.Files["issues/a.json"]));
    }

    [Fact]
    public async Task Creating_where_a_board_exists_is_refused()
    {
        using var folder = new TempFolder();
        using var store = new FolderBoardStore(folder.Path);
        await store.WriteAsync(Change(("board.json", "{}")), BoardFiles.NoBoard);

        await Assert.ThrowsAsync<BoardConflictException>(() => store.WriteAsync(Change(("board.json", "{}")), BoardFiles.NoBoard));
    }

    [Fact]
    public async Task Deleting_removes_the_file_and_empty_folders()
    {
        using var folder = new TempFolder();
        using var store = new FolderBoardStore(folder.Path);
        var v1 = await store.WriteAsync(Change(("board.json", "{}"), ("attachments/x/1-a.txt", "a")), BoardFiles.NoBoard);

        await store.WriteAsync(Change(("attachments/x/1-a.txt", null)), v1);

        Assert.False(Directory.Exists(Path.Combine(folder.Path, "attachments")));
        Assert.True(File.Exists(Path.Combine(folder.Path, "board.json")));
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("issues/../../outside.json")]
    [InlineData("/abs.json")]
    [InlineData("C:/abs.json")]
    [InlineData("issues\\a.json")]
    [InlineData(".version")]
    [InlineData("issues/a?.json")]
    [InlineData("issues//a.json")]
    public async Task Paths_outside_the_board_are_refused(string path)
    {
        using var folder = new TempFolder();
        using var store = new FolderBoardStore(folder.Path);

        await Assert.ThrowsAsync<BoardStoreException>(() => store.WriteAsync(Change(("board.json", "{}"), (path, "x")), BoardFiles.NoBoard));
        Assert.False(File.Exists(Path.Combine(folder.Path, "board.json")));
    }

    [Fact]
    public async Task Another_store_writing_the_same_folder_is_noticed()
    {
        using var folder = new TempFolder();
        using var mine = new FolderBoardStore(folder.Path);
        using var theirs = new FolderBoardStore(folder.Path);
        var v1 = await mine.WriteAsync(Change(("board.json", "{}")), BoardFiles.NoBoard);
        await mine.LoadAsync();
        var noticed = new TaskCompletionSource();
        mine.ChangedOutside += (_, _) => noticed.TrySetResult();

        await theirs.WriteAsync(Change(("issues/a.json", "A")), v1);

        Assert.Same(noticed.Task, await Task.WhenAny(noticed.Task, Task.Delay(TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public async Task Its_own_writes_are_not_reported_as_outside_changes()
    {
        using var folder = new TempFolder();
        using var store = new FolderBoardStore(folder.Path);
        var v1 = await store.WriteAsync(Change(("board.json", "{}")), BoardFiles.NoBoard);
        var noticed = 0;
        store.ChangedOutside += (_, _) => Interlocked.Increment(ref noticed);

        await store.WriteAsync(Change(("issues/a.json", "A")), v1);
        await Task.Delay(800);

        Assert.Equal(0, noticed);
    }
}
