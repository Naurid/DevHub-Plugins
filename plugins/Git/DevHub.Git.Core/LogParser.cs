using System.Globalization;

namespace DevHub.Git.Core;

/// <summary>
/// Parses <c>git log --format=</c><see cref="Format"/> <c>--decorate=full</c>: one record per
/// commit, fields separated by the ASCII unit separator (0x1F), records ended by the record
/// separator (0x1E). Those control characters can't appear in names or subjects in practice,
/// unlike tabs, "|" or newlines, so no quoting is needed.
/// </summary>
public static class LogParser
{
    private const char FieldSeparator = '\x1f';
    private const char RecordSeparator = '\x1e';

    /// <summary>
    /// Id, parents, author name / email / date, committer name / date (Unix seconds), ref names,
    /// subject. <c>%D</c> with <c>--decorate=full</c> gives full ref names
    /// ("HEAD -&gt; refs/heads/main, tag: refs/tags/v1"), so branches and tags can be told apart.
    /// </summary>
    public const string Format = "%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%cn%x1f%ct%x1f%D%x1f%s%x1e";

    public static IReadOnlyList<GitCommit> Parse(string output)
    {
        var commits = new List<GitCommit>();
        foreach (var raw in output.Split(RecordSeparator))
        {
            // git puts a newline between records; trim it (and only it) from the front.
            var record = raw.TrimStart('\r', '\n');
            if (record.Length == 0)
                continue;

            var f = record.Split(FieldSeparator);
            if (f.Length < 9)
                throw new FormatException($"Unexpected git log record: \"{record}\"");

            commits.Add(new GitCommit
            {
                Id = f[0],
                ParentIds = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                AuthorName = f[2],
                AuthorEmail = f[3],
                AuthorDate = FromUnix(f[4]),
                CommitterName = f[5],
                CommitDate = FromUnix(f[6]),
                Refs = ParseRefs(f[7]),
                // The subject is last, so a stray separator inside it can't shift other fields.
                Subject = string.Join(FieldSeparator, f[8..]),
            });
        }
        return commits;
    }

    /// <summary>
    /// "HEAD -&gt; refs/heads/main, refs/remotes/origin/main, tag: refs/tags/v1" → labels.
    /// Local branches first (the checked-out one first), then tags, then remote branches;
    /// "origin/HEAD" is left out (it only says which branch the remote considers default).
    /// </summary>
    public static IReadOnlyList<GitRefLabel> ParseRefs(string decoration)
    {
        if (string.IsNullOrWhiteSpace(decoration))
            return [];

        var labels = new List<GitRefLabel>();
        string? headBranch = null;
        var detachedHead = false;

        foreach (var part in decoration.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("HEAD -> ", StringComparison.Ordinal))
            {
                headBranch = part["HEAD -> ".Length..];
                labels.Add(Label(headBranch, isHead: true));
            }
            else if (part == "HEAD")
            {
                detachedHead = true;
            }
            else if (part.StartsWith("tag: ", StringComparison.Ordinal))
            {
                labels.Add(Label(part["tag: ".Length..]));
            }
            else
            {
                labels.Add(Label(part));
            }
        }

        if (detachedHead)
            labels.Add(new GitRefLabel(GitRefKind.Head, "HEAD", IsHead: true));

        return labels
            .Where(l => !(l.Kind == GitRefKind.RemoteBranch && l.Name.EndsWith("/HEAD", StringComparison.Ordinal)))
            .OrderBy(l => l.Kind switch
            {
                GitRefKind.Head => 0,
                GitRefKind.LocalBranch => l.IsHead ? 0 : 1,
                GitRefKind.Tag => 2,
                _ => 3,
            })
            .ToList();
    }

    private static GitRefLabel Label(string fullRef, bool isHead = false)
    {
        if (fullRef.StartsWith("refs/heads/", StringComparison.Ordinal))
            return new GitRefLabel(GitRefKind.LocalBranch, fullRef["refs/heads/".Length..], isHead);
        if (fullRef.StartsWith("refs/remotes/", StringComparison.Ordinal))
            return new GitRefLabel(GitRefKind.RemoteBranch, fullRef["refs/remotes/".Length..], isHead);
        if (fullRef.StartsWith("refs/tags/", StringComparison.Ordinal))
            return new GitRefLabel(GitRefKind.Tag, fullRef["refs/tags/".Length..], isHead);
        // Something else (refs/notes/…, a custom namespace): show it as a local-style label.
        return new GitRefLabel(GitRefKind.LocalBranch, fullRef, isHead);
    }

    private static DateTimeOffset FromUnix(string seconds) =>
        long.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s)
            : default;

    /// <summary>
    /// Parses <c>git diff-tree -r -M --name-status -z</c>: "M\0path\0", "R087\0old\0new\0" … into
    /// changes (shown like staged changes: the status letter in <see cref="FileChange.IndexStatus"/>).
    /// </summary>
    public static IReadOnlyList<FileChange> ParseNameStatus(string output)
    {
        var changes = new List<FileChange>();
        var parts = output.Split('\0');
        var i = 0;
        while (i < parts.Length)
        {
            var status = parts[i++];
            if (status.Length == 0)
                continue;

            var letter = status[0];
            if ((letter is 'R' or 'C') && i + 1 < parts.Length)
            {
                var from = parts[i++];
                var to = parts[i++];
                changes.Add(new FileChange(to, letter, '.', FileChangeKind.Renamed, from));
            }
            else if (i < parts.Length)
            {
                changes.Add(new FileChange(parts[i++], letter, '.', FileChangeKind.Ordinary));
            }
        }
        return changes;
    }
}
