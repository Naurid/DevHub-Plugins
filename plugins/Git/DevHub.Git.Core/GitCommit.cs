namespace DevHub.Git.Core;

/// <summary>One commit in the history (All Commits), from <c>git log</c> (see <see cref="LogParser"/>).</summary>
public sealed record GitCommit
{
    /// <summary>Full commit id.</summary>
    public required string Id { get; init; }

    /// <summary>Parent ids: none for a root commit, two or more for a merge. First parent first.</summary>
    public IReadOnlyList<string> ParentIds { get; init; } = [];

    public string AuthorName { get; init; } = "";
    public string AuthorEmail { get; init; } = "";
    public DateTimeOffset AuthorDate { get; init; }

    public string CommitterName { get; init; } = "";
    public DateTimeOffset CommitDate { get; init; }

    /// <summary>First line of the message.</summary>
    public string Subject { get; init; } = "";

    /// <summary>Branches, tags and HEAD pointing at this commit.</summary>
    public IReadOnlyList<GitRefLabel> Refs { get; init; } = [];

    public string ShortId => Id[..Math.Min(8, Id.Length)];
    public bool IsMerge => ParentIds.Count > 1;
}

/// <summary>What a ref label on a commit is.</summary>
public enum GitRefKind
{
    /// <summary>A detached HEAD (when HEAD is on a branch, that branch is marked <see cref="GitRefLabel.IsHead"/> instead).</summary>
    Head,
    LocalBranch,
    RemoteBranch,
    Tag,
}

/// <summary>A branch / tag badge on a commit ("main", "origin/main", "v1.0").</summary>
/// <param name="Name">Short name as git shows it: "main", "origin/main", "v1.0", "HEAD".</param>
/// <param name="IsHead">The checked-out branch (HEAD -&gt; this branch).</param>
public sealed record GitRefLabel(GitRefKind Kind, string Name, bool IsHead = false)
{
    public override string ToString() => Name;
}
