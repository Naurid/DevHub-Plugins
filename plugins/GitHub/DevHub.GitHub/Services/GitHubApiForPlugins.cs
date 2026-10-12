using DevHub.GitHub.Core;
using DevHub.Integration;

namespace DevHub.Services;

/// <summary>
/// GitHub's REST API for other plugins (<see cref="IGitHubApi"/>, DevHub.Integration): CI's runs and releases, Board's
/// GitHub Issues import. Calls go through <see cref="GitHubService"/>, which borrows the token from Git Credential
/// Manager for each call and only ever sends it to api.github.com; the token itself never leaves this plugin.
/// Without git (no Git Credential Manager) nobody can be signed in: every call says so.
/// </summary>
public sealed class GitHubApiForPlugins(GitHubService? github = null) : IGitHubApi
{
    public async Task<IReadOnlyList<string>> GetAccountsAsync(CancellationToken ct = default)
    {
        if (github is null)
            return [];
        try
        {
            return await github.ListAccountsAsync(ct);
        }
        catch (Exception ex) when (ex is GitHubException or InvalidOperationException or IOException)
        {
            return [];
        }
    }

    public async Task<GitHubApiResponse> SendAsync(GitHubApiRequest request, CancellationToken ct = default)
    {
        if (github is null)
            throw new GitHubApiUnavailableException("GitHub needs Git (Git Credential Manager keeps the sign-in). Install Git, then sign in in Settings → GitHub.");
        try
        {
            var raw = await github.SendRawAsync(request.Account, request.Method, request.Path, request.Body, request.IfNoneMatch, request.Accept, ct);
            return new GitHubApiResponse(raw.Status, raw.Body, raw.ETag, raw.NextPageUrl, raw.RateLimitRemaining, raw.RateLimitReset);
        }
        catch (GitHubException ex)
        {
            throw new GitHubApiUnavailableException(ex.Message, ex);
        }
    }
}
