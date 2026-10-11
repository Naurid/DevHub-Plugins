using DevHub.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Git.Core.Tests;

public class GitExecutableTests
{
    [Theory]
    [InlineData("git version 2.47.1.windows.2", 2, 47, 1)]
    [InlineData("git version 2.30.0\n", 2, 30, 0)]
    [InlineData("git version 2.39.3 (Apple Git-146)", 2, 39, 3)]
    [InlineData("git version 2.45", 2, 45, 0)]
    public void ParseVersion_reads_major_minor_patch(string output, int major, int minor, int patch)
    {
        Assert.Equal(new Version(major, minor, patch), GitExecutable.ParseVersion(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not git")]
    [InlineData("git: command not found")]
    public void ParseVersion_returns_null_for_other_text(string output)
    {
        Assert.Null(GitExecutable.ParseVersion(output));
    }
}

public class GitRunnerTests
{
    [GitFact]
    public async Task Returns_stdout_of_a_successful_command()
    {
        using var repo = new TempGitRepo();

        var result = await repo.Runner.RunAsync(repo.Path, ["rev-parse", "--is-inside-work-tree"]);

        Assert.True(result.Succeeded);
        Assert.Equal("true", result.StandardOutput.Trim());
    }

    [GitFact]
    public async Task Failing_command_throws_GitException_with_stderr()
    {
        using var repo = new TempGitRepo();

        var ex = await Assert.ThrowsAsync<GitException>(() =>
            repo.Runner.RunAsync(repo.Path, ["rev-parse", "--verify", "no-such-branch"]));

        Assert.NotEqual(0, ex.ExitCode);
        Assert.Equal(["rev-parse", "--verify", "no-such-branch"], ex.Arguments);
        Assert.False(string.IsNullOrWhiteSpace(ex.StandardError));
    }

    [GitFact]
    public async Task AllowFailure_returns_the_failed_result_instead_of_throwing()
    {
        using var repo = new TempGitRepo();

        var result = await repo.Runner.RunAsync(repo.Path, ["rev-parse", "--verify", "no-such-branch"],
            new GitRunOptions { AllowFailure = true });

        Assert.False(result.Succeeded);
    }

    [GitFact]
    public async Task Standard_input_reaches_git()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "hello");
        repo.Run("add", "a.txt");

        // commit -F - reads the message from stdin (how G3 will commit).
        await repo.Runner.RunAsync(repo.Path, ["commit", "-F", "-"],
            new GitRunOptions { StandardInput = "Message from stdin\n\nBody line" });

        var subject = repo.Run("log", "-1", "--format=%s").StandardOutput.Trim();
        Assert.Equal("Message from stdin", subject);
    }

    [GitFact]
    public async Task Cancelled_command_throws_and_is_logged_as_cancelled()
    {
        using var repo = new TempGitRepo();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.Runner.RunAsync(repo.Path, ["status"], ct: cts.Token));

        Assert.Equal(-1, repo.Log.Entries[^1].ExitCode);
    }

    [GitFact]
    public async Task Every_command_is_logged_with_its_repository_and_exit_code()
    {
        using var repo = new TempGitRepo();
        var before = repo.Log.Entries.Count;
        GitCommandEntry? raised = null;
        repo.Log.EntryAdded += (_, e) => raised = e;

        await repo.Runner.RunAsync(repo.Path, ["status", "--porcelain=v2"], new GitRunOptions { ReadOnly = true });

        Assert.Equal(before + 1, repo.Log.Entries.Count);
        var entry = repo.Log.Entries[^1];
        Assert.Same(entry, raised);
        Assert.Equal(repo.Path, entry.RepositoryPath);
        Assert.Equal("git status --porcelain=v2", entry.CommandLine);
        Assert.True(entry.Succeeded);
    }

    [GitFact]
    public async Task Allowed_failures_are_logged_as_expected_real_ones_are_not()
    {
        using var repo = new TempGitRepo();

        await repo.Runner.RunAsync(repo.Path, ["rev-parse", "--verify", "nope"], new GitRunOptions { AllowFailure = true });
        var probe = repo.Log.Entries[^1];
        await Assert.ThrowsAsync<GitException>(() => repo.Runner.RunAsync(repo.Path, ["rev-parse", "--verify", "nope"]));
        var failure = repo.Log.Entries[^1];

        Assert.False(probe.Succeeded);
        Assert.True(probe.FailureExpected);
        Assert.False(failure.FailureExpected);
    }

    [Fact]
    public void Log_drops_the_oldest_entries_beyond_its_capacity()
    {
        var log = new GitCommandLog(capacity: 2);
        for (var i = 0; i < 3; i++)
            log.Add(new GitCommandEntry("repo", [$"cmd{i}"], DateTimeOffset.Now, TimeSpan.Zero, 0, ""));

        Assert.Equal(["git cmd1", "git cmd2"], log.Entries.Select(e => e.CommandLine));
    }
}

public class GitRepositoryClientTests
{
    [GitFact]
    public async Task New_repository_reports_its_initial_branch()
    {
        using var repo = new TempGitRepo();

        Assert.Equal("main", await repo.Client.GetBranchAsync());
    }

    [GitFact]
    public async Task Checked_out_branch_is_reported()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1");
        repo.CommitAll("first");
        repo.Run("switch", "-c", "feature/login");

        Assert.Equal("feature/login", await repo.Client.GetBranchAsync());
    }

    [GitFact]
    public async Task Detached_head_reports_the_short_commit_id()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1");
        repo.CommitAll("first");
        repo.Run("checkout", "--detach");
        var shortId = repo.Run("rev-parse", "--short", "HEAD").StandardOutput.Trim();

        Assert.Equal(shortId, await repo.Client.GetBranchAsync());
    }

    [GitFact]
    public async Task Folder_outside_any_repository_is_not_a_repository()
    {
        using var repo = new TempGitRepo(init: false);

        Assert.False(await repo.Client.IsRepositoryAsync());
    }

    [GitFact]
    public async Task Repository_and_its_top_level_are_found_from_a_subfolder()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("src/deep/file.txt", "x");
        var client = new GitRepositoryClient(repo.Runner, Path.Combine(repo.Path, "src", "deep"));

        Assert.True(await client.IsRepositoryAsync());
        Assert.Equal(Path.GetFullPath(repo.Path), await client.GetTopLevelAsync(), ignoreCase: true);
    }

    [GitFact]
    public async Task Missing_folder_is_not_a_repository()
    {
        using var repo = new TempGitRepo();
        var client = new GitRepositoryClient(repo.Runner, Path.Combine(repo.Path, "does-not-exist"));

        Assert.False(await client.IsRepositoryAsync());
    }
}
