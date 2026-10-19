using DevHub.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Git.Core.Tests;

public class RepositoryWatcherTests
{
    [Theory]
    [InlineData("src/App.cs", true)]
    [InlineData("readme.md", true)]
    [InlineData(".gitignore", true)]
    [InlineData(".git/index", true)]
    [InlineData(".git/HEAD", true)]
    [InlineData(".git/refs/heads/main", true)]
    [InlineData(".git/MERGE_HEAD", true)]
    [InlineData(".git/rebase-merge/done", true)]
    [InlineData(@".git\packed-refs", true)]
    [InlineData(".git/objects/ab/cdef", false)]
    [InlineData(".git/logs/HEAD", false)]
    [InlineData(".git/index.lock", false)]
    [InlineData(".git", false)]
    public void Only_changes_that_affect_status_are_relevant(string relativePath, bool relevant)
    {
        Assert.Equal(relevant, RepositoryWatcher.IsRelevant(relativePath));
    }

    [Theory]
    [InlineData("src/App.cs", RepositoryChange.Files)]
    [InlineData(".git/index", RepositoryChange.Files)]
    [InlineData(".git/refs/heads/main", RepositoryChange.Refs)]
    [InlineData(".git/refs/stash", RepositoryChange.Refs)]
    [InlineData(".git/HEAD", RepositoryChange.Refs | RepositoryChange.Files)]
    [InlineData(".git/rebase-merge/done", RepositoryChange.Refs | RepositoryChange.Files)]
    [InlineData("src/.gitignore", RepositoryChange.Files | RepositoryChange.Ignores)]
    [InlineData(".git/info/exclude", RepositoryChange.Files | RepositoryChange.Ignores)]
    [InlineData(".gitmodules", RepositoryChange.Files | RepositoryChange.Refs)]
    [InlineData(".git/objects/ab/cdef", RepositoryChange.None)]
    public void Changes_say_what_they_can_affect(string relativePath, RepositoryChange change) =>
        Assert.Equal(change, RepositoryWatcher.Classify(relativePath));

    [Theory]
    [InlineData("bin/Debug/App.dll", true)]
    [InlineData("bin", true)]                        // the folder itself (created, deleted)
    [InlineData("src/App/obj/project.assets.json", true)]
    [InlineData("app.csproj.user", true)]
    [InlineData("binary/App.cs", false)]             // "bin/" isn't a prefix of "binary/"
    [InlineData("src/App.cs", false)]
    public void Ignored_folders_cover_everything_in_them(string relativePath, bool ignored)
    {
        var paths = new IgnoredPaths(["bin/", "src/App/obj/", "app.csproj.user"]);
        Assert.Equal(ignored, paths.Contains(relativePath));
        Assert.Equal(ignored ? RepositoryChange.None : RepositoryChange.Files, RepositoryWatcher.Classify(relativePath, paths));
    }

    [GitFact]
    public async Task A_build_writing_ignored_folders_raises_nothing_and_an_edit_still_does()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile(".gitignore", "bin/\nobj/\n");
        repo.WriteFile("App.cs", "class App {}");
        repo.CommitAll("start");
        repo.WriteFile("bin/Debug/App.dll", "x");
        repo.WriteFile("obj/project.assets.json", "{}");   // a folder made after the list is learnt by the tab (RepositoryDocumentViewModel)

        var ignored = await repo.Client.GetIgnoredAsync();
        Assert.Contains("bin/", ignored);

        RepositoryWatcher.SetIgnored(repo.Path, ignored);
        using var watcher = new RepositoryWatcher(repo.Path, TimeSpan.FromMilliseconds(300));
        var changes = new List<RepositoryChange>();
        watcher.Changed += (_, e) => { lock (changes) changes.Add(e.Change); };

        for (var i = 0; i < 20; i++)
            repo.WriteFile($"bin/Debug/f{i}.dll", "build output");
        repo.WriteFile("obj/project.assets.json", "{}");
        await Task.Delay(1500);
        lock (changes)
            Assert.Empty(changes);

        repo.WriteFile("App.cs", "class App { }");
        await Task.Delay(1500);
        lock (changes)
            Assert.Equal([RepositoryChange.Files], changes);
    }

    [Fact]
    public async Task A_burst_of_file_writes_raises_Changed_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "devhub-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var watcher = new RepositoryWatcher(root, TimeSpan.FromMilliseconds(500));
            var count = 0;
            watcher.Changed += (_, _) => Interlocked.Increment(ref count);

            for (var i = 0; i < 20; i++)
                await File.WriteAllTextAsync(Path.Combine(root, $"f{i}.txt"), "x");

            // Wait well past the debounce. (A generous debounce: with the other tests running git in
            // parallel, a write can stall for a few hundred ms, which would split the burst in two.)
            await Task.Delay(2500);

            Assert.Equal(1, count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
