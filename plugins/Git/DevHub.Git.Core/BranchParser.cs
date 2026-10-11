using System.Globalization;

namespace DevHub.Git.Core;

/// <summary>
/// Parses <c>git for-each-ref</c> output in the <see cref="Format"/> below: one ref per line,
/// fields separated by NUL (git writes "%00" as a NUL byte), so subjects with any characters are safe.
/// </summary>
public static class BranchParser
{
    /// <summary>
    /// refname, objectname, HEAD ("*" when current), upstream:short, upstream:track
    /// ("ahead 1, behind 2" / "gone"), committer date (unix seconds), subject, symref (set for
    /// "origin/HEAD", which isn't a branch).
    /// </summary>
    public const string Format =
        "%(refname)%00%(objectname)%00%(HEAD)%00%(upstream:short)%00%(upstream:track,nobracket)%00" +
        "%(committerdate:unix)%00%(subject)%00%(symref)";

    private const string LocalPrefix = "refs/heads/";
    private const string RemotePrefix = "refs/remotes/";

    public static IReadOnlyList<GitBranch> Parse(string output)
    {
        var branches = new List<GitBranch>();
        foreach (var line in output.Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split('\0');
            if (fields.Length < 8)
                continue;

            var (refName, commit, head, upstream, track, date, subject, symref) =
                (fields[0], fields[1], fields[2], fields[3], fields[4], fields[5], fields[6], fields[7]);
            if (symref.Length > 0)
                continue; // refs/remotes/origin/HEAD → origin/main: an alias, not a branch

            string name;
            string? remote = null;
            if (refName.StartsWith(LocalPrefix, StringComparison.Ordinal))
            {
                name = refName[LocalPrefix.Length..];
            }
            else if (refName.StartsWith(RemotePrefix, StringComparison.Ordinal))
            {
                // refs/remotes/<remote>/<branch…>; remote names can't contain "/" in practice.
                var rest = refName[RemotePrefix.Length..];
                var slash = rest.IndexOf('/');
                if (slash <= 0)
                    continue;
                remote = rest[..slash];
                name = rest[(slash + 1)..];
            }
            else
            {
                continue;
            }

            var (ahead, behind, gone) = ParseTrack(track);
            branches.Add(new GitBranch
            {
                Ref = refName,
                Name = name,
                Remote = remote,
                IsCurrent = head == "*",
                CommitId = commit,
                Upstream = upstream.Length > 0 ? upstream : null,
                Ahead = ahead,
                Behind = behind,
                UpstreamGone = gone,
                Subject = subject,
                CommitDate = long.TryParse(date, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : default,
            });
        }
        return branches;
    }

    /// <summary>"ahead 2, behind 1" / "ahead 2" / "behind 1" / "gone" / "".</summary>
    public static (int Ahead, int Behind, bool Gone) ParseTrack(string track)
    {
        if (track == "gone")
            return (0, 0, true);

        int ahead = 0, behind = 0;
        foreach (var part in track.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var words = part.Split(' ');
            if (words.Length != 2 || !int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                continue;
            if (words[0] == "ahead")
                ahead = n;
            else if (words[0] == "behind")
                behind = n;
        }
        return (ahead, behind, false);
    }
}
