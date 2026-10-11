namespace DevHub.Git.Core;

/// <summary>
/// One stash entry (<c>git stash list</c>): changes put aside with <c>git stash push</c>.
/// A stash is a commit: its first parent is the commit it was made on, the second holds the
/// staged changes, a third (when untracked files were included) the untracked files.
/// </summary>
/// <param name="Index">Position in the list: 0 is the newest ("stash@{0}"). Shifts when stashes are added or dropped.</param>
/// <param name="Commit">The stash commit (id, date, parents, subject).</param>
public sealed record GitStash(int Index, GitCommit Commit)
{
    /// <summary>"stash@{0}": how git names it right now.</summary>
    public string Ref => $"stash@{{{Index}}}";

    public string Id => Commit.Id;

    /// <summary>When it was made.</summary>
    public DateTimeOffset Date => Commit.AuthorDate;

    /// <summary>Includes untracked files (a third parent).</summary>
    public bool HasUntracked => Commit.ParentIds.Count >= 3;

    /// <summary>The branch it was made on ("main"), or null when git's subject doesn't say (detached HEAD).</summary>
    public string? Branch => Parse(Commit.Subject).Branch;

    /// <summary>
    /// The message: the one given to <c>stash push -m</c>, or for an unnamed stash git's
    /// "WIP" text with the commit it was made on ("WIP: 1a2b3c4 Fix login").
    /// </summary>
    public string Message => Parse(Commit.Subject).Message;

    /// <summary>
    /// git's stash subjects: "On main: my message" (named) or "WIP on main: 1a2b3c4 subject"
    /// (unnamed); "(no branch)" when HEAD was detached.
    /// </summary>
    public static (string? Branch, string Message) Parse(string subject)
    {
        var wip = subject.StartsWith("WIP on ", StringComparison.Ordinal);
        var prefix = wip ? "WIP on " : subject.StartsWith("On ", StringComparison.Ordinal) ? "On " : null;
        var colon = subject.IndexOf(": ", StringComparison.Ordinal);
        if (prefix is null || colon < prefix.Length)
            return (null, subject);

        var branch = subject[prefix.Length..colon];
        var message = subject[(colon + 2)..];
        return (branch == "(no branch)" ? null : branch, wip ? "WIP: " + message : message);
    }

    public override string ToString() => $"{Ref}: {Message}";
}

/// <summary>
/// A changed file and the commit its diff comes from: the commit itself, or for a stash either
/// the stash or its untracked-files part.
/// </summary>
public sealed record CommitFile(FileChange Change, GitCommit Source);

/// <summary>How applying a stash ended.</summary>
public enum StashApplyResult
{
    /// <summary>The changes are back in the working tree (and, for Pop, the stash is gone).</summary>
    Applied,

    /// <summary>
    /// Some changes clashed with the files as they are now: the conflicts are marked in the files
    /// (resolve them in Local Changes). The stash is kept, even for Pop.
    /// </summary>
    Conflicts,
}
