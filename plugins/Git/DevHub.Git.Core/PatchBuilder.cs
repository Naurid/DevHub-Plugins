using System.Text;

namespace DevHub.Git.Core;

/// <summary>What to do with selected lines of a file's diff.</summary>
public enum PartialAction
{
    /// <summary>Unstaged diff → put the selected changes into the index (<c>apply --cached</c>).</summary>
    Stage,

    /// <summary>Staged diff → take the selected changes out of the index (<c>apply --cached --reverse</c>).</summary>
    Unstage,

    /// <summary>Unstaged diff → undo the selected changes in the working tree (<c>apply --reverse</c>). Destructive.</summary>
    Discard,
}

/// <summary>
/// Builds a patch containing only some lines of a <see cref="FileDiff"/>, for staging,
/// unstaging or discarding single hunks or lines (Fork's "Stage lines", JetBrains' partial
/// commits). The patch goes to <c>git apply</c>.
/// </summary>
/// <remarks>
/// <para>A unified diff has an "old" side (the lines with " " and "-") and a "new" side
/// (" " and "+"). Leaving a change out of the patch must keep the side git checks against
/// exactly as it is, or the patch won't apply:</para>
/// <list type="bullet">
/// <item><b>Applied forwards</b> (stage): git checks the old side. An unselected "-" line stays in
/// the file, so it becomes context; an unselected "+" line was never there, so it is dropped.</item>
/// <item><b>Applied in reverse</b> (unstage, discard): git checks the new side. An unselected "+"
/// line stays, so it becomes context; an unselected "-" line is dropped.</item>
/// </list>
/// <para>Hunk headers are recomputed: the checked side keeps its start and count; the other
/// side's start moves by what earlier hunks of this patch added or removed.</para>
/// </remarks>
public static class PatchBuilder
{
    /// <summary>
    /// The patch for the selected lines, or null when no added / removed line is selected.
    /// </summary>
    /// <param name="isSelected">(hunk index, line index in that hunk) → selected. Context lines are ignored.</param>
    /// <exception cref="NotSupportedException">Binary, conflicted (combined), renamed or cut-off diffs.</exception>
    public static string? Build(FileDiff diff, Func<int, int, bool> isSelected, PartialAction action)
    {
        if (diff.IsBinary || diff.IsCombined || diff.IsDirectory)
            throw new NotSupportedException("Only text changes can be staged line by line.");
        if (diff.IsRenamed)
            throw new NotSupportedException("A renamed file can only be staged as a whole.");
        if (diff.IsTruncated)
            throw new NotSupportedException("This diff is too long to show in full, so it can only be staged as a whole.");

        var reverse = action != PartialAction.Stage;
        var body = new StringBuilder();
        var shift = 0; // how much the hunks so far moved the other side's line numbers
        var oldTotal = 0;
        var newTotal = 0;
        var anyChange = false;

        for (var h = 0; h < diff.Hunks.Count; h++)
        {
            var hunk = diff.Hunks[h];
            var lines = new StringBuilder();
            int oldCount = 0, newCount = 0, changes = 0;

            for (var i = 0; i < hunk.Lines.Count; i++)
            {
                var line = hunk.Lines[i];
                var selected = line.Kind != DiffLineKind.Context && isSelected(h, i);
                char? prefix = line.Kind switch
                {
                    DiffLineKind.Context => ' ',
                    _ when selected => line.Kind == DiffLineKind.Added ? '+' : '-',
                    // Unselected change: kept as context on the side git checks, dropped otherwise.
                    DiffLineKind.Removed => reverse ? null : ' ',
                    DiffLineKind.Added => reverse ? ' ' : null,
                    _ => null,
                };
                if (prefix is not { } p)
                    continue;

                if (p != ' ')
                    changes++;
                if (p is ' ' or '-')
                    oldCount++;
                if (p is ' ' or '+')
                    newCount++;

                lines.Append(p).Append(line.Text);
                if (line.HasCarriageReturn)
                    lines.Append('\r');
                lines.Append('\n');
                if (line.NoNewlineAtEnd)
                    lines.Append("\\ No newline at end of file\n");
            }

            if (changes == 0)
                continue; // nothing selected in this hunk: leave it out entirely

            anyChange = true;
            int oldStart, newStart;
            if (reverse)
            {
                newStart = hunk.NewStart;
                oldStart = OtherStart(hunk.NewStart, hunk.NewCount, oldCount, -shift);
            }
            else
            {
                oldStart = hunk.OldStart;
                newStart = OtherStart(hunk.OldStart, hunk.OldCount, newCount, shift);
            }
            shift += newCount - oldCount;
            oldTotal += oldCount;
            newTotal += newCount;

            body.Append($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@\n").Append(lines);
        }

        if (!anyChange)
            return null;

        // Header. A new file keeps "new file mode" only while the patch's old side is still
        // empty (all of it is applied); a deletion keeps "deleted file mode" only while the new
        // side is. Otherwise it's an ordinary change to the file.
        var path = diff.NewPath ?? diff.OldPath ?? throw new NotSupportedException("The diff has no path.");
        var isNewFile = diff.IsNewFile && oldTotal == 0;
        var isDeletion = diff.IsDeleted && newTotal == 0;

        var header = new StringBuilder($"diff --git a/{path} b/{path}\n");
        if (isNewFile)
            header.Append($"new file mode {diff.NewFileMode ?? "100644"}\n");
        if (isDeletion)
            header.Append($"deleted file mode {diff.DeletedFileMode ?? "100644"}\n");
        header.Append(isNewFile ? "--- /dev/null\n" : $"--- a/{path}\n");
        header.Append(isDeletion ? "+++ /dev/null\n" : $"+++ b/{path}\n");

        return header.Append(body).ToString();
    }

    /// <summary>
    /// Start line on the side that changes, given the fixed side's start and count. Git's
    /// convention: a side with 0 lines names the line <i>before</i> the hunk instead of its first.
    /// </summary>
    private static int OtherStart(int fixedStart, int fixedCount, int otherCount, int shift)
    {
        // First line of the hunk on the fixed side (for an empty side: the line after the one named).
        var first = fixedCount == 0 ? fixedStart + 1 : fixedStart;
        var start = first + shift;
        return otherCount == 0 ? start - 1 : start;
    }

    /// <summary>True when every added / removed line of the diff is selected (then stage the whole file instead).</summary>
    public static bool SelectsEverything(FileDiff diff, Func<int, int, bool> isSelected)
    {
        for (var h = 0; h < diff.Hunks.Count; h++)
            for (var i = 0; i < diff.Hunks[h].Lines.Count; i++)
                if (diff.Hunks[h].Lines[i].Kind != DiffLineKind.Context && !isSelected(h, i))
                    return false;
        return true;
    }
}
