namespace DevHub.ViewModels.Git;

/// <summary>
/// A hosting service for repositories (GitHub; later maybe GitLab, Bitbucket) that the Git plugin
/// can use without knowing it: offered by a plugin that requires Git (GitHub registers one), and
/// optional in Git (injected as <c>IRemoteHost? = null</c>). Git does the git work (add remote,
/// push, clone); the host does the account and its own API. Without one, Git still works with
/// remotes by URL.
/// </summary>
public interface IRemoteHost
{
    /// <summary>Shown in labels: "Publish to {Name}…", "Your {Name} repositories".</summary>
    string Name { get; }

    /// <summary>The host can be reached at all (e.g. GitHub needs git's credential manager).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Creates an empty repository on the host for Publish, asking what's needed (account, name,
    /// visibility, the remote's name) in the host's own dialog. Null when cancelled or refused
    /// (the host has told the user why).
    /// </summary>
    /// <param name="suggestedName">The local repository's folder name.</param>
    /// <param name="existingRemotes">Remote names already in use (the new one must differ).</param>
    Task<CreatedHostRepository?> CreateRepositoryAsync(string suggestedName, IReadOnlyList<string> existingRemotes);

    /// <summary>You on the host (name, picture, which commit e-mails are yours), or null when not signed in / offline.</summary>
    Task<HostIdentity?> GetMyIdentityAsync();

    /// <summary>The signed-in accounts (empty when none); problems are shown by the host.</summary>
    Task<IReadOnlyList<string>> ListAccountsAsync();

    /// <summary>The host's sign-in; true when an account is now signed in.</summary>
    Task<bool> SignInAsync();

    /// <summary>An account's repositories, for Clone.</summary>
    Task<IReadOnlyList<HostRepository>> ListRepositoriesAsync(string account, CancellationToken ct);
}

/// <summary>A repository made by <see cref="IRemoteHost.CreateRepositoryAsync"/>: Git adds it as <paramref name="RemoteName"/> and pushes.</summary>
public sealed record CreatedHostRepository(string RemoteName, string CloneUrl, string WebUrl);

/// <summary>A repository on the host, as listed in Clone.</summary>
public sealed record HostRepository(string Name, string FullName, string? Description, bool IsPrivate, string CloneUrl,
    DateTimeOffset? PushedAt);

/// <summary>
/// Who you are on the host, so All Commits can show your picture on your commits.
/// </summary>
/// <param name="Names">Your login and display name (commit author names that are you).</param>
/// <param name="Avatar">PNG / JPEG bytes, or null.</param>
/// <param name="IsOwnEmail">Whether a commit e-mail is one of yours on the host (e.g. GitHub's no-reply address).</param>
public sealed record HostIdentity(IReadOnlyList<string> Names, byte[]? Avatar, Func<string, bool> IsOwnEmail);
