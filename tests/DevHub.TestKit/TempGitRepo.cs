using DevHub.Git.Core;
using Xunit;

namespace DevHub.TestKit;

/// <summary>[Fact] that is skipped (not failed) when git isn't installed.</summary>
public sealed class GitFactAttribute : FactAttribute
{
    public GitFactAttribute()
    {
        if (TempGitRepo.Git is null)
            Skip = "git is not installed (or older than " + GitExecutable.MinimumVersion + ").";
    }
}

/// <summary>[Theory] that is skipped (not failed) when git isn't installed.</summary>
public sealed class GitTheoryAttribute : TheoryAttribute
{
    public GitTheoryAttribute()
    {
        if (TempGitRepo.Git is null)
            Skip = "git is not installed (or older than " + GitExecutable.MinimumVersion + ").";
    }
}

/// <summary>
/// A throw-away git repository in %TEMP%, isolated from the user's own git setup: an empty
/// global config, no system config, fixed author/committer and dates, so output is the same on
/// every machine. Dispose deletes it.
/// </summary>
public sealed class TempGitRepo : IDisposable
{
    /// <summary>The git found on this machine (null → [GitFact] tests are skipped).</summary>
    public static readonly GitExecutable? Git = GitExecutable.TryLocate();

    private readonly string _root;

    public TempGitRepo(bool init = true)
    {
        _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devhub-git-" + Guid.NewGuid().ToString("N"));
        Path = System.IO.Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path);

        var emptyConfig = System.IO.Path.Combine(_root, "empty.gitconfig");
        File.WriteAllText(emptyConfig, "");

        Environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_GLOBAL"] = emptyConfig,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_AUTHOR_NAME"] = "Test Author",
            ["GIT_AUTHOR_EMAIL"] = "author@example.com",
            ["GIT_AUTHOR_DATE"] = "2026-01-01T12:00:00+00:00",
            ["GIT_COMMITTER_NAME"] = "Test Committer",
            ["GIT_COMMITTER_EMAIL"] = "committer@example.com",
            ["GIT_COMMITTER_DATE"] = "2026-01-01T12:00:00+00:00",
        };

        Log = new GitCommandLog();
        Runner = new GitRunner(Git!, Log, Environment);
        Client = new GitRepositoryClient(Runner, Path);

        if (init)
            Run("init", "-b", "main");
    }

    /// <summary>The repository folder.</summary>
    public string Path { get; }

    public IReadOnlyDictionary<string, string?> Environment { get; }
    public GitCommandLog Log { get; }
    public GitRunner Runner { get; }
    public GitRepositoryClient Client { get; }

    /// <summary>Runs git in the repository (synchronously, for test setup).</summary>
    public GitResult Run(params string[] arguments) =>
        Runner.RunAsync(Path, arguments).GetAwaiter().GetResult();

    /// <summary>Writes a file (relative path, forward slashes allowed), creating folders.</summary>
    public void WriteFile(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Stages everything and commits.</summary>
    public void CommitAll(string message)
    {
        Run("add", "-A");
        Run("commit", "-m", message);
    }

    public void Dispose()
    {
        try
        {
            // git marks object files read-only; clear that so the folder can be deleted.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp folder isn't worth failing a test over.
        }
    }
}
