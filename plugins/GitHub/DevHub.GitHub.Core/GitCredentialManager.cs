using DevHub.Git.Core;

namespace DevHub.GitHub.Core;

/// <summary>
/// The GitHub accounts known to Git Credential Manager (GCM, part of Git for Windows): the same
/// sign-in git uses when pushing to github.com. DevHub never stores a token itself; it asks GCM
/// for one when it calls the GitHub API and keeps it in memory only.
/// </summary>
/// <remarks>
/// Runs <c>git credential-manager …</c> and <c>git credential fill</c> through <see cref="GitRunner"/>,
/// so they show up in the Git Console (the token-returning one with its output hidden).
/// </remarks>
public sealed class GitCredentialManager(GitRunner runner)
{
    private const string GitHubHost = "github.com";

    /// <summary>Folder the commands run in: they don't need a repository.</summary>
    private static string WorkingDirectory => Path.GetTempPath();

    /// <summary>GCM is installed (git knows the <c>credential-manager</c> command).</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(WorkingDirectory, ["credential-manager", "--version"],
            new GitRunOptions { ReadOnly = true, AllowFailure = true }, ct);
        return result.Succeeded;
    }

    /// <summary>The GitHub accounts signed in through GCM (user names), in GCM's order.</summary>
    public async Task<IReadOnlyList<string>> ListGitHubAccountsAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(WorkingDirectory, ["credential-manager", "github", "list"],
            new GitRunOptions { ReadOnly = true }, ct);
        return ParseAccounts(result.StandardOutput);
    }

    /// <summary>One account per line; blank lines ignored.</summary>
    public static IReadOnlyList<string> ParseAccounts(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>
    /// Signs in to GitHub with GCM's own window (browser, device code or token; the user picks),
    /// then waits until it's done. Cancelling closes it. Afterwards the account is in
    /// <see cref="ListGitHubAccountsAsync"/> and git pushes to github.com use it.
    /// </summary>
    public async Task SignInAsync(CancellationToken ct = default) =>
        await runner.RunAsync(WorkingDirectory, ["credential-manager", "github", "login"],
            new GitRunOptions { Interactive = true }, ct);

    /// <summary>Removes an account's stored sign-in from GCM (git will ask again on the next push).</summary>
    public async Task SignOutAsync(string account, CancellationToken ct = default) =>
        await runner.RunAsync(WorkingDirectory, ["credential-manager", "github", "logout", account], ct: ct);

    /// <summary>
    /// The account's GitHub token, from GCM's store (<c>git credential fill</c>). Never shows a
    /// window: null when there's no stored sign-in (sign in again). The output is kept out of
    /// the Git Console; callers must not log or save the token either.
    /// </summary>
    public async Task<string?> GetTokenAsync(string account, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(WorkingDirectory, ["credential", "fill"],
            new GitRunOptions
            {
                StandardInput = $"protocol=https\nhost={GitHubHost}\nusername={account}\n\n",
                AllowFailure = true,
                Sensitive = true,
                // Fail instead of opening a sign-in window: signing in is its own, explicit action.
                Environment = new Dictionary<string, string?> { ["GCM_INTERACTIVE"] = "never" },
            }, ct);
        return result.Succeeded ? ParsePassword(result.StandardOutput) : null;
    }

    /// <summary>The <c>password=</c> value of git's credential output, or null.</summary>
    public static string? ParsePassword(string output) =>
        output.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => l.StartsWith("password=", StringComparison.Ordinal))?["password=".Length..] is { Length: > 0 } p
            ? p
            : null;
}
