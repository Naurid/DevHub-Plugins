using System.Globalization;

namespace DevHub.Git.Core;

/// <summary>The commit a blamed line comes from (JetBrains' "Annotate with Git Blame").</summary>
public sealed record BlameCommit(string Id, string Author, string AuthorEmail, DateTimeOffset AuthorDate, string Summary)
{
    /// <summary>The line was changed after the last commit (git's all-zero id, author "Not Committed Yet").</summary>
    public bool IsUncommitted => Id.All(c => c == '0');

    public string ShortId => Id[..Math.Min(8, Id.Length)];
}

/// <summary>One line of a blamed file: its number (from 1), text, and the commit that last changed it.</summary>
public sealed record BlameLine(int LineNumber, string Text, BlameCommit Commit);

/// <summary>
/// Parses <c>git blame --porcelain</c>. Each line comes as a header ("&lt;id&gt; &lt;original
/// line&gt; &lt;final line&gt; [&lt;lines in group&gt;]"), then, the first time a commit
/// appears, "key value" lines about it (author, author-time, summary, …), then the line's text
/// after a tab. Later lines from the same commit repeat only the header.
/// </summary>
public static class BlameParser
{
    public static IReadOnlyList<BlameLine> Parse(string output)
    {
        var commits = new Dictionary<string, Builder>(StringComparer.Ordinal);
        var lines = new List<BlameLine>();
        Builder? current = null;
        var finalLine = 0;

        foreach (var raw in output.Split('\n'))
        {
            if (raw.StartsWith('\t'))
            {
                // The line's text. A CRLF file keeps its "\r" here: drop it, like an editor would.
                if (current is not null)
                    lines.Add(new BlameLine(finalLine, raw[1..].TrimEnd('\r'), current.Build()));
                current = null;
                continue;
            }

            if (current is null)
            {
                // Header: "<id> <orig> <final> [<count>]".
                var parts = raw.Split(' ');
                if (parts.Length < 3 || !IsCommitId(parts[0])
                    || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out finalLine))
                    continue;
                if (!commits.TryGetValue(parts[0], out current))
                    commits[parts[0]] = current = new Builder(parts[0]);
                continue;
            }

            var space = raw.IndexOf(' ');
            var key = space < 0 ? raw : raw[..space];
            var value = space < 0 ? "" : raw[(space + 1)..];
            switch (key)
            {
                case "author":
                    current.Author = value;
                    break;
                case "author-mail":
                    current.Email = value.Trim('<', '>');
                    break;
                case "author-time":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                        current.Date = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    break;
                case "summary":
                    current.Summary = value;
                    break;
            }
        }
        return lines;
    }

    private static bool IsCommitId(string text) =>
        text.Length is 40 or 64 && text.All(Uri.IsHexDigit);

    /// <summary>Collects a commit's details across lines; builds one shared record.</summary>
    private sealed class Builder(string id)
    {
        private BlameCommit? _built;
        public string Author { get; set; } = "";
        public string Email { get; set; } = "";
        public DateTimeOffset Date { get; set; }
        public string Summary { get; set; } = "";

        // Every detail line comes before the commit's first text line, so building then is complete.
        public BlameCommit Build() => _built ??= new BlameCommit(id, Author, Email, Date, Summary);
    }
}
