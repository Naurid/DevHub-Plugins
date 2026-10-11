namespace DevHub.Git.Core;

/// <summary>Where a submodule's checkout stands compared with the commit the parent repository records.</summary>
public enum SubmoduleState
{
    /// <summary>Checked out at the recorded commit.</summary>
    UpToDate,

    /// <summary>Not cloned yet (empty folder): Update Submodules fetches it.</summary>
    NotInitialized,

    /// <summary>Checked out at another commit than the one recorded (moved on, or the parent was updated).</summary>
    DifferentCommit,

    /// <summary>The recorded commit is in a merge conflict.</summary>
    Conflict,
}

/// <summary>A submodule: another repository checked out in a folder of this one.</summary>
/// <param name="Path">Folder, relative to the repository root (forward slashes).</param>
/// <param name="CommitId">The commit checked out (or, when not initialized, the one recorded).</param>
/// <param name="Describe">git's name for that commit ("v1.2-3-g1a2b3c4", "heads/main"), or null.</param>
public sealed record GitSubmodule(string Path, string CommitId, SubmoduleState State, string? Describe)
{
    public string ShortId => CommitId[..Math.Min(8, CommitId.Length)];
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('/')) is { Length: > 0 } name ? name : Path;
    public override string ToString() => Path;
}

/// <summary>
/// Parses <c>git submodule status</c>: one line per submodule, "&lt;state&gt;&lt;id&gt; &lt;path&gt; (&lt;describe&gt;)"
/// where the state character is " " (up to date), "-" (not initialized), "+" (other commit) or "U" (conflict).
/// </summary>
public static class SubmoduleParser
{
    public static IReadOnlyList<GitSubmodule> Parse(string output)
    {
        var submodules = new List<GitSubmodule>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 3)
                continue;

            var state = line[0] switch
            {
                '-' => SubmoduleState.NotInitialized,
                '+' => SubmoduleState.DifferentCommit,
                'U' => SubmoduleState.Conflict,
                _ => SubmoduleState.UpToDate,
            };
            var rest = line[1..];
            var space = rest.IndexOf(' ');
            if (space <= 0)
                continue;
            var id = rest[..space];
            var path = rest[(space + 1)..];

            // A path can contain spaces, so take the describe part from the end: " (…)".
            string? describe = null;
            var open = path.LastIndexOf(" (", StringComparison.Ordinal);
            if (path.EndsWith(')') && open > 0)
            {
                describe = path[(open + 2)..^1];
                path = path[..open];
            }
            submodules.Add(new GitSubmodule(path, id, state, describe));
        }
        return submodules;
    }
}
