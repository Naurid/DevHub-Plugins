namespace DevHub.Projects.Core;

/// <summary>
/// Scans one or more root folders and reports the git repositories found beneath them.
/// This is the seam the UI depends on; the implementation does all the file-system work.
/// </summary>
public interface IRepositoryScanner
{
    /// <summary>
    /// Walks each root recursively and returns every git repository found, de-duplicated by
    /// path and sorted. Roots that don't exist are skipped. Folders that can't be read are
    /// skipped rather than aborting the scan.
    /// </summary>
    /// <param name="roots">Absolute folder paths to scan.</param>
    /// <param name="ct">Cancels the scan.</param>
    Task<IReadOnlyList<GitRepository>> ScanAsync(
        IEnumerable<string> roots,
        CancellationToken ct = default);
}
