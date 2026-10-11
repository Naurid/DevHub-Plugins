namespace DevHub.Git.Core;

/// <summary>How a merge / rebase / continue / skip ended.</summary>
public enum OperationOutcome
{
    /// <summary>Finished: no operation in progress any more.</summary>
    Done,

    /// <summary>Stopped on conflicts: resolve them, then continue (or abort).</summary>
    Conflicts,

    /// <summary>Nothing to do (already up to date / already on top).</summary>
    UpToDate,
}

/// <summary>Which version of a conflicted file to keep.</summary>
/// <remarks>
/// Named from the user's point of view (JetBrains' "Accept Yours / Accept Theirs"). Git's
/// <c>--ours</c> / <c>--theirs</c> swap meaning during a rebase: there "ours" is the branch being
/// rebased <i>onto</i> and "theirs" is your commit being replayed. <see cref="GitRepositoryClient.ResolveAsync"/>
/// does the mapping, so callers never deal with it.
/// </remarks>
public enum ConflictChoice
{
    /// <summary>Your version: the current branch in a merge; your commit in a rebase.</summary>
    Yours,

    /// <summary>The incoming version: the merged branch in a merge; the branch rebased onto in a rebase.</summary>
    Theirs,
}

/// <summary>Conflict markers in file content ("&lt;&lt;&lt;&lt;&lt;&lt;&lt; HEAD" … "&gt;&gt;&gt;&gt;&gt;&gt;&gt; branch").</summary>
public static class ConflictMarkers
{
    /// <summary>True when a line starts with seven &lt; and another with seven &gt; (git's markers).</summary>
    public static bool Contains(string text)
    {
        var start = false;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("<<<<<<<", StringComparison.Ordinal))
                start = true;
            else if (start && line.StartsWith(">>>>>>>", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
