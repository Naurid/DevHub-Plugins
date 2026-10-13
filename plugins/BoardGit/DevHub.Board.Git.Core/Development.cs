using System.Globalization;
using DevHub.Git.Core;
using DevHub.Sdk.Core.Settings;

namespace DevHub.Board.Git.Core;

/// <summary>A branch that names an issue.</summary>
public sealed record IssueBranch(string Name, bool IsRemote, string CommitSha);

/// <summary>A commit whose message names an issue.</summary>
public sealed record IssueCommit(string Sha, string ShortSha, string Author, DateTimeOffset Date, string Subject);

/// <summary>An issue's development: its branches and commits in one repository.</summary>
public sealed record DevelopmentInfo(IReadOnlyList<IssueBranch> Branches, IReadOnlyList<IssueCommit> Commits)
{
    public static DevelopmentInfo Empty { get; } = new([], []);
}

/// <summary>
/// Reads which branches and commits of a repository name issues (read-only git; the board's own branch is left out,
/// its commit messages name issues too).
/// </summary>
public sealed class DevelopmentQueries(GitRunner runner)
{
    private static readonly GitRunOptions ReadOnly = new() { ReadOnly = true };

    // --all without the board's branch (local and remote copies).
    private static readonly string[] AllButBoard =
        ["--exclude=refs/heads/" + BranchBoardStore.BranchName, "--exclude=refs/remotes/*/" + BranchBoardStore.BranchName, "--all"];

    /// <summary>Branches (local and remote) whose name contains <paramref name="key"/>.</summary>
    public async Task<IReadOnlyList<IssueBranch>> BranchesAsync(string repository, string projectKey, string key, CancellationToken ct = default)
    {
        var refs = await RefsAsync(repository, ct);
        return refs.Where(r => IssueKeys.Find(r.Name, projectKey).Contains(key, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Every branch, for decorating them (name → its keys is the caller's business).</summary>
    public async Task<IReadOnlyList<IssueBranch>> RefsAsync(string repository, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(repository,
            ["for-each-ref", "--format=%(refname)%00%(objectname)", "refs/heads", "refs/remotes"], ReadOnly, ct);
        var branches = new List<IssueBranch>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\0');
            if (parts.Length < 2 || parts[0].EndsWith("/HEAD", StringComparison.Ordinal))
                continue;
            var full = parts[0];
            var remote = full.StartsWith("refs/remotes/", StringComparison.Ordinal);
            var name = remote ? full["refs/remotes/".Length..] : full["refs/heads/".Length..];
            if (name == BranchBoardStore.BranchName || name.EndsWith("/" + BranchBoardStore.BranchName, StringComparison.Ordinal) && remote)
                continue;
            branches.Add(new IssueBranch(name, remote, parts[1].Trim()));
        }
        return branches;
    }

    /// <summary>Commits (newest first, up to <paramref name="limit"/>) whose message names <paramref name="key"/>.</summary>
    public async Task<IReadOnlyList<IssueCommit>> CommitsAsync(string repository, string key, int limit = 100, CancellationToken ct = default)
    {
        var pattern = $"(^|[^A-Za-z0-9]){System.Text.RegularExpressions.Regex.Escape(key)}([^0-9]|$)";
        return await LogAsync(repository, pattern, limit, ct);
    }

    /// <summary>Commits naming any issue of the project (for decorations), newest first.</summary>
    public async Task<IReadOnlyList<IssueCommit>> ProjectCommitsAsync(string repository, string projectKey, int limit = 2000, CancellationToken ct = default) =>
        await LogAsync(repository, $"(^|[^A-Za-z0-9]){projectKey}-[0-9]+", limit, ct);

    private async Task<IReadOnlyList<IssueCommit>> LogAsync(string repository, string pattern, int limit, CancellationToken ct)
    {
        var result = await runner.RunAsync(repository,
            ["log", .. AllButBoard, "-E", "-i", "--grep=" + pattern, "-n", limit.ToString(CultureInfo.InvariantCulture),
                "--format=%H%x00%h%x00%an%x00%aI%x00%s%x1e"],
            new GitRunOptions { ReadOnly = true, AllowFailure = true }, ct);
        if (!result.Succeeded)
            return [];   // a repository without commits
        var commits = new List<IssueCommit>();
        foreach (var record in result.StandardOutput.Split('\x1e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = record.Split('\0');
            if (p.Length < 5)
                continue;
            commits.Add(new IssueCommit(p[0], p[1], p[2],
                DateTimeOffset.TryParse(p[3], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTimeOffset.MinValue, p[4]));
        }
        return commits;
    }

    /// <summary>Both at once, for an issue view.</summary>
    public async Task<DevelopmentInfo> ForIssueAsync(string repository, string projectKey, string key, CancellationToken ct = default) =>
        new(await BranchesAsync(repository, projectKey, key, ct), await CommitsAsync(repository, key, 100, ct));

    /// <summary>The repository's top folder, or null when <paramref name="folder"/> isn't inside one.</summary>
    public async Task<string?> RepositoryOfAsync(string folder, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder))
            return null;
        var result = await runner.RunAsync(folder, ["rev-parse", "--show-toplevel"], new GitRunOptions { ReadOnly = true, AllowFailure = true }, ct);
        return result.Succeeded ? Path.GetFullPath(result.StandardOutput.Trim()) : null;
    }
}

/// <summary>Board.Git's settings: which repository a board's issues are developed in (when it isn't obvious).</summary>
public sealed class BoardGitSettings : ISettingsSection
{
    public static string SectionId => "devhub.board.git";

    /// <summary>Board location key ("folder|C:\…") → repository folder.</summary>
    public Dictionary<string, string> Repositories { get; set; } = new();

    /// <summary>Show smart commit commands found in a commit message for confirmation.</summary>
    public bool SmartCommits { get; set; } = true;

    /// <summary>Suggest "DEV-12: " in Git's commit box on an issue's branch.</summary>
    public bool SuggestCommitMessage { get; set; } = true;
}
