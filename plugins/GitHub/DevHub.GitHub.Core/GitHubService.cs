using DevHub.Sdk.Core.Localization;

namespace DevHub.GitHub.Core;

/// <summary>
/// GitHub for one signed-in account: the API calls with the token borrowed from Git Credential
/// Manager for each call (held in memory only for that call, never saved or logged).
/// </summary>
public sealed class GitHubService(GitCredentialManager credentials, GitHubApi api)
{
    public GitCredentialManager Credentials { get; } = credentials;

    public Task<IReadOnlyList<string>> ListAccountsAsync(CancellationToken ct = default) =>
        Credentials.ListGitHubAccountsAsync(ct);

    public async Task<GitHubUser> GetUserAsync(string account, CancellationToken ct = default) =>
        await api.GetUserAsync(await TokenAsync(account, ct), ct);

    /// <summary>The account's picture (no token involved: avatars are public), or null.</summary>
    public Task<byte[]?> GetAvatarAsync(GitHubUser user, int size = 96, CancellationToken ct = default) =>
        user.AvatarUrl is { } url ? api.GetAvatarAsync(url, size, ct) : Task.FromResult<byte[]?>(null);

    public async Task<IReadOnlyList<GitHubRepository>> ListRepositoriesAsync(string account, CancellationToken ct = default) =>
        await api.ListRepositoriesAsync(await TokenAsync(account, ct), ct: ct);

    public async Task<GitHubRepository> CreateRepositoryAsync(string account, string name, string? description, bool isPrivate,
        CancellationToken ct = default) =>
        await api.CreateRepositoryAsync(await TokenAsync(account, ct), name, description, isPrivate, ct);

    /// <summary>
    /// Any API call as <paramref name="account"/> (null: the only or first signed-in account), for other plugins
    /// (<see cref="GitHubApi.SendRawAsync"/>). The token is borrowed for this call only.
    /// </summary>
    public async Task<GitHubRawResponse> SendRawAsync(string? account, string method, string pathOrUrl, string? json = null,
        string? ifNoneMatch = null, string? accept = null, CancellationToken ct = default)
    {
        account ??= (await ListAccountsAsync(ct)).FirstOrDefault()
                    ?? throw new GitHubException(L.T("Nobody is signed in to GitHub. Sign in in Settings → GitHub."));
        return await api.SendRawAsync(await TokenAsync(account, ct), method, pathOrUrl, json, ifNoneMatch, accept, ct);
    }

    private async Task<string> TokenAsync(string account, CancellationToken ct) =>
        await Credentials.GetTokenAsync(account, ct)
        ?? throw new GitHubException(L.T($"Git Credential Manager has no stored sign-in for {account}. Sign in again in Settings → GitHub."));

    /// <summary>
    /// Whether <paramref name="name"/> is a repository name GitHub accepts: letters, digits,
    /// "-", "_" and "."; at most 100 characters; not "." or "..".
    /// </summary>
    public static bool IsValidRepositoryName(string name) =>
        name.Length is > 0 and <= 100 && name is not ("." or "..")
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
