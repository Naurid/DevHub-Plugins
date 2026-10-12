namespace DevHub.Git.Core;

/// <summary>What kind of status line a <see cref="FileChange"/> came from.</summary>
public enum FileChangeKind
{
    /// <summary>Changed tracked file (porcelain v2 line "1").</summary>
    Ordinary,

    /// <summary>Renamed or copied (line "2"); <see cref="FileChange.OriginalPath"/> is set.</summary>
    Renamed,

    /// <summary>Merge conflict (line "u").</summary>
    Unmerged,

    /// <summary>Not tracked by git (line "?"). A folder ends with "/".</summary>
    Untracked,
}

/// <summary>
/// One changed file. Status letters are git's: <c>.</c> unchanged, <c>M</c> modified,
/// <c>T</c> type changed, <c>A</c> added, <c>D</c> deleted, <c>R</c> renamed, <c>C</c> copied,
/// <c>U</c> updated but unmerged. <see cref="IndexStatus"/> is the staged side,
/// <see cref="WorkTreeStatus"/> the unstaged side; one file can be both (staged, then edited again).
/// </summary>
/// <param name="Path">Git path: relative to the repository root, forward slashes.</param>
/// <param name="OriginalPath">For renames/copies, the path it came from; otherwise null.</param>
public sealed record FileChange(
    string Path,
    char IndexStatus,
    char WorkTreeStatus,
    FileChangeKind Kind,
    string? OriginalPath = null)
{
    /// <summary>Has changes in the index (would be committed).</summary>
    public bool IsStaged => Kind is FileChangeKind.Ordinary or FileChangeKind.Renamed && IndexStatus != '.';

    /// <summary>Has changes only in the working tree (or isn't tracked yet), i.e. not staged.</summary>
    public bool IsUnstaged => Kind == FileChangeKind.Untracked
                              || Kind is FileChangeKind.Ordinary or FileChangeKind.Renamed && WorkTreeStatus != '.';

    public bool IsConflicted => Kind == FileChangeKind.Unmerged;

    /// <summary>
    /// The paths to hand <c>git add</c> to stage this file's unstaged side: its path, plus the old
    /// path only when the rename itself is unstaged (then git must also record the old path's removal).
    /// A rename that's already staged and then edited again ("RM") has no old path left anywhere,
    /// and naming it would make git refuse the whole command ("pathspec did not match").
    /// </summary>
    public IReadOnlyList<string> StagePaths =>
        OriginalPath is { } from && WorkTreeStatus is 'R' or 'C' ? [Path, from] : [Path];

    /// <summary>
    /// The paths to hand <c>git reset</c> to unstage this file: its path, plus the old path when the
    /// rename is staged (so the old file comes back into the index and the rename is fully undone).
    /// </summary>
    public IReadOnlyList<string> UnstagePaths =>
        OriginalPath is { } from && IndexStatus is 'R' or 'C' ? [Path, from] : [Path];

    /// <summary>File name without folders ("App.axaml"); for an untracked folder its name.</summary>
    public string FileName => System.IO.Path.GetFileName(Path.TrimEnd('/')) is { Length: > 0 } name ? name : Path;

    /// <summary>Folder part ("src/DevHub.App"), or "" at the repository root.</summary>
    public string Directory
    {
        get
        {
            var trimmed = Path.TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            return slash < 0 ? "" : trimmed[..slash];
        }
    }
}

/// <summary>An operation git is in the middle of (needs continue / abort).</summary>
public enum RepositoryOperation
{
    None,
    Merging,
    Rebasing,
    CherryPicking,
    Reverting,
}

/// <summary>Result of <c>git status --porcelain=v2 --branch</c>.</summary>
public sealed record RepoStatus
{
    /// <summary>Checked-out branch, or null when HEAD is detached.</summary>
    public string? Branch { get; init; }

    /// <summary>Current commit id, or null in a repository with no commits yet.</summary>
    public string? CommitId { get; init; }

    /// <summary>Upstream branch ("origin/main"), or null.</summary>
    public string? Upstream { get; init; }

    /// <summary>Commits ahead of / behind the upstream (0 without an upstream).</summary>
    public int Ahead { get; init; }
    public int Behind { get; init; }

    public bool IsDetached => Branch is null && CommitId is not null;

    public IReadOnlyList<FileChange> Changes { get; init; } = [];

    /// <summary>What the title bar / tab shows: the branch, or the short commit id when detached.</summary>
    public string? DisplayBranch => Branch ?? CommitId?[..Math.Min(7, CommitId.Length)];
}
