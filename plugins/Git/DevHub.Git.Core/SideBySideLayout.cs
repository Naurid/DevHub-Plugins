namespace DevHub.Git.Core;

/// <summary>
/// One row of a side-by-side diff: a hunk header, or the old file's line on the left and the
/// new file's line on the right. A side is null where it has no line (a filler), e.g. the left
/// of an added line.
/// </summary>
/// <param name="HunkIndex">Which hunk of the file the row belongs to.</param>
/// <param name="LeftIndex">The left line's index in its hunk's <see cref="DiffHunk.Lines"/>, or -1.</param>
/// <param name="RightIndex">The right line's index in its hunk's lines, or -1 (the same as
/// <paramref name="LeftIndex"/> for a context line, which is one line shown on both sides).</param>
public sealed record SideBySideRow(int HunkIndex, DiffLine? Left, DiffLine? Right, int LeftIndex = -1, int RightIndex = -1)
{
    /// <summary>The hunk this header row starts, or null for a line row.</summary>
    public DiffHunk? Hunk { get; init; }

    public bool IsHunkHeader => Hunk is not null;
}

/// <summary>
/// Arranges a unified diff in two columns, like JetBrains' side-by-side viewer: context lines
/// on both sides; a block of removed lines followed by added lines is lined up row by row
/// (the first removed line next to the first added one, …), the shorter side padded with
/// fillers. Pure function of the parsed diff, so it is tested without git.
/// </summary>
public static class SideBySideLayout
{
    public static IReadOnlyList<SideBySideRow> Build(FileDiff diff)
    {
        var rows = new List<SideBySideRow>();
        for (var h = 0; h < diff.Hunks.Count; h++)
        {
            var hunk = diff.Hunks[h];
            rows.Add(new SideBySideRow(h, null, null) { Hunk = hunk });

            var lines = hunk.Lines;
            var i = 0;
            while (i < lines.Count)
            {
                if (lines[i].Kind == DiffLineKind.Context)
                {
                    rows.Add(new SideBySideRow(h, lines[i], lines[i], i, i));
                    i++;
                    continue;
                }

                // One change block: the removed lines, then the added lines that replace them.
                var removedStart = i;
                while (i < lines.Count && lines[i].Kind == DiffLineKind.Removed)
                    i++;
                var addedStart = i;
                while (i < lines.Count && lines[i].Kind == DiffLineKind.Added)
                    i++;
                var removed = addedStart - removedStart;
                var added = i - addedStart;

                for (var k = 0; k < Math.Max(removed, added); k++)
                {
                    var left = k < removed ? removedStart + k : -1;
                    var right = k < added ? addedStart + k : -1;
                    rows.Add(new SideBySideRow(h, left >= 0 ? lines[left] : null, right >= 0 ? lines[right] : null,
                        left, right));
                }
            }
        }
        return rows;
    }
}
