namespace DevHub.Git.Core;

/// <summary>A local or remote-tracking branch, from <c>git for-each-ref</c> (see <see cref="BranchParser"/>).</summary>
public sealed record GitBranch
{
    /// <summary>Full ref ("refs/heads/main", "refs/remotes/origin/main").</summary>
    public required string Ref { get; init; }

    /// <summary>Branch name without the remote ("main", "feature/x").</summary>
    public required string Name { get; init; }

    /// <summary>Remote name ("origin") for a remote-tracking branch; null for a local one.</summary>
    public string? Remote { get; init; }

    public bool IsRemote => Remote is not null;

    /// <summary>What git calls it on the command line: "main" or "origin/main".</summary>
    public string ShortName => Remote is null ? Name : $"{Remote}/{Name}";

    /// <summary>The checked-out branch.</summary>
    public bool IsCurrent { get; init; }

    public required string CommitId { get; init; }

    /// <summary>Upstream of a local branch ("origin/main"), or null.</summary>
    public string? Upstream { get; init; }

    /// <summary>Commits ahead of / behind the upstream (0 without one).</summary>
    public int Ahead { get; init; }
    public int Behind { get; init; }

    /// <summary>The upstream was deleted on the remote (git shows "[gone]").</summary>
    public bool UpstreamGone { get; init; }

    /// <summary>Subject (first line) of the branch's last commit.</summary>
    public string Subject { get; init; } = "";

    /// <summary>When the branch's last commit was made.</summary>
    public DateTimeOffset CommitDate { get; init; }
}

/// <summary>How a checkout went.</summary>
public enum CheckoutResult
{
    /// <summary>Switched.</summary>
    Done,

    /// <summary>
    /// Not switched: local changes would be overwritten. Nothing changed; the caller can offer
    /// <see cref="GitRepositoryClient.SmartCheckoutAsync"/> (stash, switch, re-apply).
    /// </summary>
    LocalChangesInTheWay,
}

/// <summary>How a smart checkout (stash, switch, re-apply) went.</summary>
public enum SmartCheckoutResult
{
    /// <summary>Switched, and the local changes are back on top.</summary>
    Done,

    /// <summary>
    /// Switched, but re-applying the changes conflicted: the conflicts are in the working tree
    /// and the stash was kept (git keeps it when "stash pop" conflicts), so nothing is lost.
    /// </summary>
    ReapplyConflicted,
}
