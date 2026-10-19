using System.Text.RegularExpressions;

namespace DevHub.Ci.Core;

/// <summary>A line of a job's log as DevHub shows it.</summary>
/// <param name="IsError">GitHub marked it (<c>##[error]</c>): highlighted.</param>
/// <param name="IsHeader">Where a step starts (<c>##[group]Run …</c>): a heading.</param>
public sealed record LogLine(string Text, bool IsError, bool IsHeader);

/// <summary>
/// The part of a job's raw log worth reading when it failed: the sections (a step starts at a top-level
/// <c>##[group]</c>) that hold an error, with GitHub's timestamps stripped. A log without error markers gives its last
/// lines (the end of a log is where things went wrong).
/// </summary>
public static partial class LogExcerpt
{
    public const int MaxLines = 600;
    private const int TailWhenNoErrors = 200;

    [GeneratedRegex(@"^\uFEFF?\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z ")]
    private static partial Regex Timestamp();

    /// <summary>Every line of <paramref name="rawLog"/>, without timestamps, errors and step starts marked.</summary>
    public static IReadOnlyList<LogLine> Lines(string rawLog)
    {
        var lines = new List<LogLine>();
        var depth = 0;
        foreach (var raw in rawLog.Replace("\r\n", "\n").Split('\n'))
        {
            var text = Timestamp().Replace(raw, "");
            if (text.StartsWith("##[group]", StringComparison.Ordinal))
            {
                lines.Add(new LogLine(text["##[group]".Length..], false, depth == 0));
                depth++;
            }
            else if (text.StartsWith("##[endgroup]", StringComparison.Ordinal))
                depth = Math.Max(0, depth - 1);
            else if (text.StartsWith("##[error]", StringComparison.Ordinal))
                lines.Add(new LogLine(text["##[error]".Length..], true, false));
            else
                lines.Add(new LogLine(text, false, false));
        }
        while (lines.Count > 0 && lines[^1].Text.Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>The failed sections of <paramref name="rawLog"/> (at most <see cref="MaxLines"/> lines, the end kept).</summary>
    public static IReadOnlyList<LogLine> Failed(string rawLog)
    {
        var lines = Lines(rawLog);
        // Sections: from one step start to the next.
        var starts = lines.Select((l, i) => (l, i)).Where(x => x.l.IsHeader).Select(x => x.i).ToList();
        if (starts.Count == 0 || starts[0] != 0)
            starts.Insert(0, 0);
        var picked = new List<LogLine>();
        for (var s = 0; s < starts.Count; s++)
        {
            var end = s + 1 < starts.Count ? starts[s + 1] : lines.Count;
            var section = lines.Skip(starts[s]).Take(end - starts[s]).ToList();
            if (section.Any(l => l.IsError))
                picked.AddRange(section);
        }
        if (picked.Count == 0)
            picked = lines.Skip(Math.Max(0, lines.Count - TailWhenNoErrors)).ToList();
        return picked.Count > MaxLines ? picked.Skip(picked.Count - MaxLines).ToList() : picked;
    }
}
