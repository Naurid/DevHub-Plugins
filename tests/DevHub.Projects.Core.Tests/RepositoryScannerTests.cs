using DevHub.Projects.Core;
using Xunit;

namespace DevHub.Projects.Core.Tests;

/// <summary>
/// Exercises <see cref="RepositoryScanner"/> against throwaway directory trees created under
/// the temp folder. Each test builds its own tree via <see cref="TempTree"/> and deletes it
/// when done.
/// </summary>
public class RepositoryScannerTests
{
    private static readonly RepositoryScanner Scanner = new(new ProjectLanguageDetector());

    [Fact]
    public async Task Finds_repo_with_dot_git_directory()
    {
        using var tree = new TempTree();
        tree.MakeRepoDir("Alpha");

        var repos = await Scanner.ScanAsync(new[] { tree.Root });

        var repo = Assert.Single(repos);
        Assert.Equal("Alpha", repo.Name);
        Assert.Equal(tree.At("Alpha"), repo.Path);
    }

    [Fact]
    public async Task Finds_repo_with_dot_git_file()
    {
        // Worktrees and submodules use a .git *file* pointing elsewhere.
        using var tree = new TempTree();
        tree.MakeRepoFile("Worktree");

        var repos = await Scanner.ScanAsync(new[] { tree.Root });

        var repo = Assert.Single(repos);
        Assert.Equal("Worktree", repo.Name);
    }

    [Fact]
    public async Task Finds_multiple_repos_in_sibling_subfolders()
    {
        using var tree = new TempTree();
        tree.MakeRepoDir("Alpha");
        tree.MakeRepoDir("nested/Beta");
        tree.MakeDir("nested/plain"); // not a repo

        var repos = await Scanner.ScanAsync(new[] { tree.Root });

        Assert.Equal(2, repos.Count);
        Assert.Contains(repos, r => r.Name == "Alpha");
        Assert.Contains(repos, r => r.Name == "Beta");
    }

    [Fact]
    public async Task Does_not_descend_into_a_found_repo()
    {
        // A repo nested inside another repo must not be listed separately (stop-at-root).
        using var tree = new TempTree();
        tree.MakeRepoDir("Outer");
        tree.MakeRepoDir("Outer/Inner");

        var repos = await Scanner.ScanAsync(new[] { tree.Root });

        var repo = Assert.Single(repos);
        Assert.Equal("Outer", repo.Name);
    }

    [Fact]
    public async Task Skips_node_modules()
    {
        using var tree = new TempTree();
        tree.MakeRepoDir("node_modules/some-pkg"); // should be ignored entirely

        var repos = await Scanner.ScanAsync(new[] { tree.Root });

        Assert.Empty(repos);
    }

    [Fact]
    public async Task Missing_root_is_skipped_without_throwing()
    {
        using var tree = new TempTree();
        tree.MakeRepoDir("Alpha");
        var missing = Path.Combine(tree.Root, "does-not-exist");

        var repos = await Scanner.ScanAsync(new[] { missing, tree.Root });

        Assert.Single(repos); // only Alpha, no exception from the missing root
    }

    [Fact]
    public async Task De_dupes_overlapping_roots()
    {
        using var tree = new TempTree();
        tree.MakeRepoDir("Alpha");

        // Pass both the parent and the repo folder itself — Alpha must appear once.
        var repos = await Scanner.ScanAsync(new[] { tree.Root, tree.At("Alpha") });

        Assert.Single(repos);
    }

    /// <summary>Creates a unique temp directory and tears it down on dispose.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; }

        public TempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "devhub-scan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        /// <summary>Resolves a '/'-separated relative path against the tree root.</summary>
        public string At(string relative) =>
            Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        public void MakeDir(string relative) => Directory.CreateDirectory(At(relative));

        /// <summary>Makes a repo folder (contains a ".git" directory).</summary>
        public void MakeRepoDir(string relative)
        {
            MakeDir(relative);
            Directory.CreateDirectory(Path.Combine(At(relative), ".git"));
        }

        /// <summary>Makes a repo folder whose ".git" is a file (worktree/submodule form).</summary>
        public void MakeRepoFile(string relative)
        {
            MakeDir(relative);
            File.WriteAllText(Path.Combine(At(relative), ".git"), "gitdir: /somewhere/else");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
