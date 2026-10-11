using System.Globalization;

namespace DevHub.Git.Core;

/// <summary>
/// Parses <c>git status --porcelain=v2 --branch -z</c>. The format is git's stable,
/// machine-readable one (documented in <c>git help status</c>, "Porcelain Format Version 2"):
/// <code>
/// # branch.oid &lt;commit&gt; | (initial)
/// # branch.head &lt;branch&gt; | (detached)
/// # branch.upstream &lt;upstream&gt;
/// # branch.ab +&lt;ahead&gt; -&lt;behind&gt;
/// 1 &lt;XY&gt; &lt;sub&gt; &lt;mH&gt; &lt;mI&gt; &lt;mW&gt; &lt;hH&gt; &lt;hI&gt; &lt;path&gt;
/// 2 &lt;XY&gt; &lt;sub&gt; &lt;mH&gt; &lt;mI&gt; &lt;mW&gt; &lt;hH&gt; &lt;hI&gt; &lt;X&gt;&lt;score&gt; &lt;path&gt;  NUL  &lt;origPath&gt;
/// u &lt;XY&gt; &lt;sub&gt; &lt;m1&gt; &lt;m2&gt; &lt;m3&gt; &lt;mW&gt; &lt;h1&gt; &lt;h2&gt; &lt;h3&gt; &lt;path&gt;
/// ? &lt;path&gt;
/// ! &lt;path&gt;
/// </code>
/// With <c>-z</c> every entry ends with NUL and paths are never quoted, so a path is simply
/// "everything after the fixed number of fields", spaces included.
/// </summary>
public static class StatusParser
{
    public static RepoStatus Parse(string output)
    {
        string? branch = null, commit = null, upstream = null;
        int ahead = 0, behind = 0;
        var changes = new List<FileChange>();

        var entries = output.Split('\0');
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length == 0)
                continue;

            switch (entry[0])
            {
                case '#':
                    ParseHeader(entry, ref branch, ref commit, ref upstream, ref ahead, ref behind);
                    break;

                case '1':
                {
                    // 8 fields before the path.
                    var parts = entry.Split(' ', 9);
                    changes.Add(new FileChange(parts[8], parts[1][0], parts[1][1], FileChangeKind.Ordinary));
                    break;
                }

                case '2':
                {
                    // 9 fields before the path; with -z the original path is the next entry.
                    var parts = entry.Split(' ', 10);
                    var original = i + 1 < entries.Length ? entries[++i] : null;
                    changes.Add(new FileChange(parts[9], parts[1][0], parts[1][1], FileChangeKind.Renamed, original));
                    break;
                }

                case 'u':
                {
                    // 10 fields before the path.
                    var parts = entry.Split(' ', 11);
                    changes.Add(new FileChange(parts[10], parts[1][0], parts[1][1], FileChangeKind.Unmerged));
                    break;
                }

                case '?':
                    changes.Add(new FileChange(entry[2..], '.', '?', FileChangeKind.Untracked));
                    break;

                // '!' = ignored files: only listed with --ignored, which DevHub doesn't ask for.
            }
        }

        return new RepoStatus
        {
            Branch = branch,
            CommitId = commit,
            Upstream = upstream,
            Ahead = ahead,
            Behind = behind,
            Changes = changes,
        };
    }

    private static void ParseHeader(string entry, ref string? branch, ref string? commit, ref string? upstream,
        ref int ahead, ref int behind)
    {
        // "# branch.<key> <value>"
        var parts = entry.Split(' ', 3);
        if (parts.Length < 3)
            return;

        var value = parts[2];
        switch (parts[1])
        {
            case "branch.oid":
                commit = value == "(initial)" ? null : value;
                break;
            case "branch.head":
                branch = value == "(detached)" ? null : value;
                break;
            case "branch.upstream":
                upstream = value;
                break;
            case "branch.ab":
                // "+3 -1"
                var counts = value.Split(' ');
                if (counts.Length == 2)
                {
                    ahead = int.Parse(counts[0].TrimStart('+'), CultureInfo.InvariantCulture);
                    behind = int.Parse(counts[1].TrimStart('-'), CultureInfo.InvariantCulture);
                }
                break;
        }
    }
}
