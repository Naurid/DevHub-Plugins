namespace DevHub.Git.Core;

/// <summary>What a diff line is: unchanged context, or added / removed by the change.</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed,
}

/// <summary>
/// One line of a diff. Line numbers are 1-based; <see cref="OldLineNumber"/> is null for an
/// added line and <see cref="NewLineNumber"/> for a removed one.
/// </summary>
/// <param name="Text">The line without its "+"/"-"/" " prefix and without a trailing CR.</param>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLineNumber, int? NewLineNumber)
{
    /// <summary>Git's "\ No newline at end of file" followed this line.</summary>
    public bool NoNewlineAtEnd { get; init; }

    /// <summary>The line ended in CR (a CRLF file). <see cref="Text"/> doesn't include it; patches put it back.</summary>
    public bool HasCarriageReturn { get; init; }
}

/// <summary>
/// A block of changes with some context around it ("@@ -12,7 +12,8 @@ void Foo()").
/// </summary>
/// <param name="Section">What git puts after the second "@@" (usually the enclosing function), or "".</param>
public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, string Section,
    IReadOnlyList<DiffLine> Lines)
{
    /// <summary>The header as git prints it, for display.</summary>
    public string Header => $"@@ -{OldStart},{OldCount} +{NewStart},{NewCount} @@" +
                            (Section.Length > 0 ? " " + Section : "");
}

/// <summary>The changes to one file, parsed from <c>git diff</c> (see <see cref="DiffParser"/>).</summary>
public sealed class FileDiff
{
    /// <summary>Path before the change (git path, forward slashes); null for a new file.</summary>
    public string? OldPath { get; init; }

    /// <summary>Path after the change; null for a deleted file.</summary>
    public string? NewPath { get; init; }

    /// <summary>The path to show: the new one, or the old one for a deleted file.</summary>
    public string Path => NewPath ?? OldPath ?? "";

    public bool IsNewFile { get; init; }
    public bool IsDeleted { get; init; }
    public bool IsRenamed => OldPath is not null && NewPath is not null && OldPath != NewPath;

    /// <summary>Git found the file binary ("Binary files … differ"): there are no lines to show.</summary>
    public bool IsBinary { get; init; }

    /// <summary>An untracked folder (status lists it as one entry): nothing to diff until it's expanded.</summary>
    public bool IsDirectory { get; init; }

    /// <summary>A merge-conflict ("combined") diff: lines can come from either side; old line numbers are omitted.</summary>
    public bool IsCombined { get; init; }

    /// <summary>Mode of a new file ("100644"), from "new file mode"; null otherwise.</summary>
    public string? NewFileMode { get; init; }

    /// <summary>Mode of a deleted file, from "deleted file mode"; null otherwise.</summary>
    public string? DeletedFileMode { get; init; }

    /// <summary>Only file mode changed (e.g. the executable bit): no line changes.</summary>
    public string? ModeChange { get; init; }

    /// <summary>The diff was longer than the limit and was cut off; <see cref="Hunks"/> holds the beginning.</summary>
    public bool IsTruncated { get; init; }

    public IReadOnlyList<DiffHunk> Hunks { get; init; } = [];

    /// <summary>
    /// Line in the new file where the first change is, to open the file there: the first added
    /// line, or for a removal the line that now sits where the removed text was. Null without hunks.
    /// </summary>
    public int? FirstChangedLine
    {
        get
        {
            foreach (var hunk in Hunks)
            {
                var next = hunk.NewStart; // new-file line number of the next line in the hunk
                foreach (var line in hunk.Lines)
                {
                    switch (line.Kind)
                    {
                        case DiffLineKind.Added:
                            return line.NewLineNumber;
                        case DiffLineKind.Removed:
                            return Math.Max(1, next);
                        default:
                            next = (line.NewLineNumber ?? next) + 1;
                            break;
                    }
                }
            }
            return null;
        }
    }

    public int AddedCount => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Added));
    public int RemovedCount => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Removed));
}
