using System.Globalization;
using System.Text;

namespace DevHub.Git.Core;

/// <summary>
/// Parses git's unified diff output (<c>git diff</c>, <c>diff --cached</c>, <c>diff --no-index</c>)
/// into one <see cref="FileDiff"/> per file. Handles new / deleted / renamed / binary files,
/// mode changes, "\ No newline at end of file", CRLF lines, C-quoted paths and combined
/// ("diff --cc") diffs of conflicted files.
/// </summary>
/// <remarks>
/// Expects the a/ and b/ prefixes (the client passes <c>--src-prefix=a/ --dst-prefix=b/</c> so
/// a user's <c>diff.noprefix</c> setting can't change the format) and no color.
/// </remarks>
public static class DiffParser
{
    /// <summary>Default cap on lines per file, so a huge generated file can't freeze the UI.</summary>
    public const int DefaultMaxLines = 20_000;

    public static IReadOnlyList<FileDiff> Parse(string output, int maxLinesPerFile = DefaultMaxLines)
    {
        var files = new List<FileDiff>();
        FileBuilder? file = null;
        HunkBuilder? hunk = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var hasCr = rawLine.EndsWith('\r');
            var line = hasCr ? rawLine[..^1] : rawLine;

            if (line.StartsWith("diff --git ", StringComparison.Ordinal)
                || line.StartsWith("diff --cc ", StringComparison.Ordinal)
                || line.StartsWith("diff --combined ", StringComparison.Ordinal))
            {
                Finish();
                file = new FileBuilder(maxLinesPerFile) { IsCombined = !line.StartsWith("diff --git ", StringComparison.Ordinal) };
                file.HeaderPath = PathFromDiffLine(line);
                continue;
            }
            if (file is null)
                continue; // anything before the first file header (shouldn't happen)

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                file.CloseHunk(hunk);
                hunk = ParseHunkHeader(line);
                continue;
            }

            if (hunk is not null)
            {
                if (hunk.TryAdd(line, hasCr, file))
                    continue;
                // Not a hunk line: the hunk is over (e.g. the next file's header follows).
                file.CloseHunk(hunk);
                hunk = null;
            }

            ParseHeaderLine(line, file);
        }

        Finish();
        return files;

        void Finish()
        {
            if (file is null)
                return;
            file.CloseHunk(hunk);
            hunk = null;
            files.Add(file.Build());
            file = null;
        }
    }

    private static void ParseHeaderLine(string line, FileBuilder file)
    {
        if (line.StartsWith("--- ", StringComparison.Ordinal))
            file.OldPath = PathAfterPrefix(line[4..]);
        else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            file.NewPath = PathAfterPrefix(line[4..]);
        else if (line.StartsWith("rename from ", StringComparison.Ordinal) || line.StartsWith("copy from ", StringComparison.Ordinal))
            file.OldPath = Unquote(line[(line.IndexOf(" from ", StringComparison.Ordinal) + 6)..]);
        else if (line.StartsWith("rename to ", StringComparison.Ordinal) || line.StartsWith("copy to ", StringComparison.Ordinal))
            file.NewPath = Unquote(line[(line.IndexOf(" to ", StringComparison.Ordinal) + 4)..]);
        else if (line.StartsWith("new file mode ", StringComparison.Ordinal))
        {
            file.IsNewFile = true;
            file.NewFileMode = line["new file mode ".Length..];
        }
        else if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
        {
            file.IsDeleted = true;
            file.DeletedFileMode = line["deleted file mode ".Length..];
        }
        else if (line.StartsWith("old mode ", StringComparison.Ordinal))
            file.OldMode = line[9..];
        else if (line.StartsWith("new mode ", StringComparison.Ordinal))
            file.NewMode = line[9..];
        else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch")
            file.IsBinary = true;
        // index …, similarity index …, dissimilarity index …: nothing we need.
    }

    // "@@ -12,7 +12,8 @@ section" or, combined, "@@@ -1,3 -1,3 +1,5 @@@ section".
    // A missing count means 1 ("@@ -3 +3 @@").
    private static HunkBuilder ParseHunkHeader(string line)
    {
        var ats = 0;
        while (ats < line.Length && line[ats] == '@')
            ats++;
        var marker = new string('@', ats);
        var end = line.IndexOf(marker, ats, StringComparison.Ordinal);
        var ranges = (end < 0 ? line[ats..] : line[ats..end]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var section = end < 0 ? "" : line[(end + ats)..].Trim();

        var oldRange = ranges.FirstOrDefault(r => r.StartsWith('-')) ?? "-0,0";
        var newRange = ranges.LastOrDefault(r => r.StartsWith('+')) ?? "+0,0";
        var (oldStart, oldCount) = ParseRange(oldRange[1..]);
        var (newStart, newCount) = ParseRange(newRange[1..]);
        return new HunkBuilder(oldStart, oldCount, newStart, newCount, section, prefixWidth: ats - 1);
    }

    private static (int Start, int Count) ParseRange(string range)
    {
        var comma = range.IndexOf(',');
        return comma < 0
            ? (int.Parse(range, CultureInfo.InvariantCulture), 1)
            : (int.Parse(range[..comma], CultureInfo.InvariantCulture),
                int.Parse(range[(comma + 1)..], CultureInfo.InvariantCulture));
    }

    /// <summary>"a/src/App.cs" → "src/App.cs"; "/dev/null" → null; quoted paths are unquoted.</summary>
    private static string? PathAfterPrefix(string value)
    {
        // Some git versions end the name with a tab when it contains spaces.
        value = value.TrimEnd('\t');
        if (value == "/dev/null")
            return null;
        var path = Unquote(value);
        return path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)
            ? path[2..]
            : path;
    }

    /// <summary>
    /// The path from "diff --git a/P b/P", for files without ---/+++ lines (binary, mode-only,
    /// pure renames). Only reliable when both sides are the same path, which is the case for
    /// those; renames get their paths from the "rename from/to" lines anyway.
    /// </summary>
    private static string? PathFromDiffLine(string line)
    {
        var rest = line[(line.IndexOf(' ', 5) + 1)..];
        if (rest.StartsWith('"'))
        {
            // diff --git "a/x y" "b/x y"
            var close = FindClosingQuote(rest);
            return close < 0 ? null : PathAfterPrefix(rest[..(close + 1)]);
        }
        if (!rest.StartsWith("a/", StringComparison.Ordinal))
            return rest.Length > 0 ? rest : null; // diff --cc path
        var length = (rest.Length - 5) / 2; // "a/" + P + " b/" + P
        if (length <= 0)
            return null;
        var path = rest.Substring(2, length);
        return rest == "a/" + path + " b/" + path ? path : null;
    }

    private static int FindClosingQuote(string value)
    {
        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] == '\\')
                i++;
            else if (value[i] == '"')
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Undoes git's C-style quoting ("\"tab\\there\"", octal UTF-8 bytes like \303\251).
    /// Unquoted values are returned as they are.
    /// </summary>
    public static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            return value;

        var bytes = new List<byte>();
        for (var i = 1; i < value.Length - 1; i++)
        {
            var c = value[i];
            if (c != '\\' || i + 1 >= value.Length - 1)
            {
                // Keep surrogate pairs (emoji…) together when turning them into UTF-8.
                var length = char.IsHighSurrogate(c) && i + 1 < value.Length - 1 ? 2 : 1;
                bytes.AddRange(Encoding.UTF8.GetBytes(value.Substring(i, length)));
                i += length - 1;
                continue;
            }

            var next = value[++i];
            if (next is >= '0' and <= '7' && i + 2 < value.Length - 1)
            {
                bytes.Add(Convert.ToByte(value.Substring(i, 3), 8));
                i += 2;
                continue;
            }
            bytes.Add(next switch
            {
                'n' => (byte)'\n',
                't' => (byte)'\t',
                'r' => (byte)'\r',
                'a' => 7,
                'b' => 8,
                'f' => 12,
                'v' => 11,
                _ => (byte)next, // \" and \\
            });
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private sealed class FileBuilder(int maxLines)
    {
        private readonly List<DiffHunk> _hunks = [];

        public string? HeaderPath { get; set; }
        public string? OldPath { get; set; }
        public string? NewPath { get; set; }
        public bool IsNewFile { get; set; }
        public bool IsDeleted { get; set; }
        public bool IsBinary { get; set; }
        public bool IsCombined { get; init; }
        public string? OldMode { get; set; }
        public string? NewMode { get; set; }
        public string? NewFileMode { get; set; }
        public string? DeletedFileMode { get; set; }
        public int LineCount { get; set; }
        public bool IsTruncated { get; set; }
        public int MaxLines => maxLines;

        public void CloseHunk(HunkBuilder? hunk)
        {
            if (hunk is { Lines.Count: > 0 })
                _hunks.Add(hunk.Build());
        }

        public FileDiff Build() => new()
        {
            OldPath = IsNewFile ? null : OldPath ?? HeaderPath,
            NewPath = IsDeleted ? null : NewPath ?? HeaderPath,
            IsNewFile = IsNewFile,
            IsDeleted = IsDeleted,
            IsBinary = IsBinary,
            IsCombined = IsCombined,
            NewFileMode = NewFileMode,
            DeletedFileMode = DeletedFileMode,
            ModeChange = OldMode is not null && NewMode is not null ? $"{OldMode} → {NewMode}" : null,
            IsTruncated = IsTruncated,
            Hunks = _hunks,
        };
    }

    private sealed class HunkBuilder(int oldStart, int oldCount, int newStart, int newCount, string section, int prefixWidth)
    {
        // The starts are copied into fields and the parameters aren't used again: a primary-
        // constructor parameter that's both copied and read later is two variables that drift
        // apart (warning CS9124).
        private readonly int _oldStart = oldStart;
        private readonly int _newStart = newStart;

        // Running line numbers of the next old / new line.
        private int _nextOld = oldStart;
        private int _nextNew = newStart;

        public List<DiffLine> Lines { get; } = [];

        /// <summary>Adds a hunk line; false when the line isn't part of a hunk.</summary>
        /// <param name="hasCarriageReturn">The raw line ended in CR (a CRLF file); kept so a patch can be rebuilt byte for byte.</param>
        public bool TryAdd(string line, bool hasCarriageReturn, FileBuilder file)
        {
            if (line.StartsWith('\\'))
            {
                // "\ No newline at end of file" belongs to the line before it.
                if (Lines.Count > 0)
                    Lines[^1] = Lines[^1] with { NoNewlineAtEnd = true };
                return true;
            }
            if (line.Length < prefixWidth)
                return false; // the empty string after the last newline, or a header
            var prefix = line[..prefixWidth];
            if (prefix.Any(c => c is not (' ' or '+' or '-')))
                return false;

            if (file.LineCount >= file.MaxLines)
            {
                file.IsTruncated = true;
                return true; // swallow the rest of the hunk
            }
            file.LineCount++;

            var text = line[prefixWidth..];
            if (prefix.Contains('+'))
                Lines.Add(new DiffLine(DiffLineKind.Added, text, null, _nextNew++) { HasCarriageReturn = hasCarriageReturn });
            else if (prefix.Contains('-'))
                Lines.Add(new DiffLine(DiffLineKind.Removed, text, prefixWidth == 1 ? _nextOld++ : null, null) { HasCarriageReturn = hasCarriageReturn });
            else
                Lines.Add(new DiffLine(DiffLineKind.Context, text, prefixWidth == 1 ? _nextOld++ : null, _nextNew++) { HasCarriageReturn = hasCarriageReturn });
            return true;
        }

        public DiffHunk Build() => new(_oldStart, oldCount, _newStart, newCount, section, Lines);
    }
}
