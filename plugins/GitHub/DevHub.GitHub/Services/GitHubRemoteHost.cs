using DevHub.ViewModels.Git;

namespace DevHub.Services;

/// <summary>
/// The GitHub plugin's <see cref="IRemoteHost"/>: what Git asks of "the hosting service", answered
/// with the GitHub account (<see cref="GitHubSession"/>) and the GitHub API. GitHub requires Git,
/// so it may use Git's types; Git never names GitHub.
/// </summary>
public sealed class GitHubRemoteHost(GitHubSession session, IDialogService dialogs) : IRemoteHost
{
    public string Name => "GitHub";

    public bool IsAvailable => session.IsAvailable;

    public async Task<CreatedHostRepository?> CreateRepositoryAsync(string suggestedName, IReadOnlyList<string> existingRemotes)
    {
        if (session.GitHub is not { } github)
            return null;
        if (await session.RequireAccountsAsync("publish this repository on GitHub") is not { Count: > 0 } accounts)
            return null;

        var dialog = new PublishToGitHubDialogViewModel(github, accounts, suggestedName, existingRemotes);
        if (!await dialogs.ShowDialogAsync(dialog) || dialog.Created is not { } created)
            return null;
        return new CreatedHostRepository(dialog.RemoteName, created.CloneUrl, created.HtmlUrl);
    }

    public async Task<HostIdentity?> GetMyIdentityAsync()
    {
        if (await session.GetMyProfileAsync() is not { } profile)
            return null;

        var login = profile.User.Login;
        var names = profile.User.Name is { Length: > 0 } name ? new[] { login, name } : new[] { login };
        // GitHub's no-reply addresses: login@users.noreply.github.com and 12345+login@users.noreply.github.com.
        return new HostIdentity(names, profile.Avatar, email =>
            email.Equals(login + "@users.noreply.github.com", StringComparison.OrdinalIgnoreCase)
            || email.EndsWith("+" + login + "@users.noreply.github.com", StringComparison.OrdinalIgnoreCase));
    }

    public Task<IReadOnlyList<string>> ListAccountsAsync() => session.ListAccountsAsync();

    public Task<bool> SignInAsync() => session.SignInAsync();

    public async Task<IReadOnlyList<HostRepository>> ListRepositoriesAsync(string account, CancellationToken ct)
    {
        if (session.GitHub is not { } github)
            return [];
        var repositories = await github.ListRepositoriesAsync(account, ct);
        return repositories
            .Select(r => new HostRepository(r.Name, r.FullName, r.Description, r.IsPrivate, r.CloneUrl, r.PushedAt))
            .ToList();
    }
}
