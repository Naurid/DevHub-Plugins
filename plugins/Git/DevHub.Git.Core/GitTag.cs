using System.Globalization;

namespace DevHub.Git.Core;

/// <summary>A tag (<c>refs/tags/…</c>): a fixed name for a commit, usually a release ("v1.2.0").</summary>
public sealed record GitTag
{
    /// <summary>"v1.2.0" (without "refs/tags/").</summary>
    public required string Name { get; init; }

    /// <summary>The commit it points at (for an annotated tag: the tagged commit, not the tag object).</summary>
    public required string CommitId { get; init; }

    /// <summary>Annotated (has its own message, author and date) rather than lightweight (just a name).</summary>
    public bool IsAnnotated { get; init; }

    /// <summary>The tag message's first line (annotated), or the tagged commit's subject (lightweight).</summary>
    public string Subject { get; init; } = "";

    /// <summary>When it was tagged (annotated), or the commit's date (lightweight).</summary>
    public DateTimeOffset Date { get; init; }

    public string ShortId => CommitId[..Math.Min(8, CommitId.Length)];

    public override string ToString() => Name;
}

/// <summary>Parses <c>git for-each-ref refs/tags --format=</c><see cref="Format"/>.</summary>
public static class TagParser
{
    private const string TagPrefix = "refs/tags/";

    /// <summary>
    /// refname, object type ("tag" = annotated, "commit" = lightweight), object id, the commit
    /// an annotated tag points at, date (tagger date, or committer date for a lightweight tag),
    /// subject (of the tag message, or of the commit). NUL-separated, one tag per line.
    /// </summary>
    public const string Format =
        "%(refname)%00%(objecttype)%00%(objectname)%00%(*objectname)%00%(creatordate:unix)%00%(contents:subject)";

    public static IReadOnlyList<GitTag> Parse(string output)
    {
        var tags = new List<GitTag>();
        foreach (var line in output.Split('\n'))
        {
            var f = line.TrimEnd('\r').Split('\0');
            if (f.Length < 6 || !f[0].StartsWith(TagPrefix, StringComparison.Ordinal))
                continue;

            var annotated = f[1] == "tag";
            tags.Add(new GitTag
            {
                Name = f[0][TagPrefix.Length..],
                IsAnnotated = annotated,
                // An annotated tag's own id isn't a commit; *objectname is what it tags.
                CommitId = annotated && f[3].Length > 0 ? f[3] : f[2],
                Date = long.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : default,
                Subject = string.Join('\0', f[5..]),
            });
        }
        return tags;
    }
}
