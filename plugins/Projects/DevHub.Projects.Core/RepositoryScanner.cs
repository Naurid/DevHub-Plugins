using DevHub.Projects.Core;

namespace DevHub.Projects.Core;

/// <summary>
/// Default <see cref="IRepositoryScanner"/>: an iterative (stack-based) directory walk that
/// records a repository as soon as it sees a <c>.git</c> entry and then stops descending into
/// it. Access errors on individual folders are swallowed so one protected directory can't
/// abort the whole scan. Each found repo gets its language detected via
/// <see cref="IProjectLanguageDetector"/>.
/// </summary>
public sealed class RepositoryScanner : IRepositoryScanner
{
    // Folders we never descend into. Kept deliberately tiny — we already stop at repo roots,
    // so the only thing this guards against is huge non-repo trees.
    private static readonly HashSet<string> IgnoredDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase) { "node_modules" };

    private readonly IProjectLanguageDetector _languageDetector;

    public RepositoryScanner(IProjectLanguageDetector languageDetector)
    {
        _languageDetector = languageDetector;
    }

    public Task<IReadOnlyList<GitRepository>> ScanAsync(
        IEnumerable<string> roots,
        CancellationToken ct = default)
    {
        // Snapshot the roots on the caller's thread, then do the blocking disk walk off the
        // UI thread so the app stays responsive during a scan.
        var rootList = roots?.ToList() ?? new List<string>();
        return Task.Run<IReadOnlyList<GitRepository>>(() => Scan(rootList, ct), ct);
    }

    private IReadOnlyList<GitRepository> Scan(List<string> roots, CancellationToken ct)
    {
        // Keyed by full path so overlapping roots don't produce duplicates.
        var found = new Dictionary<string, GitRepository>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();

        foreach (var root in roots)
        {
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                stack.Push(Path.GetFullPath(root));
        }

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            if (IsGitRepository(dir))
            {
                found.TryAdd(dir, new GitRepository(GetFolderName(dir), dir, _languageDetector.Detect(dir)));
                continue; // stop-at-root: don't list nested/submodule repos for now
            }

            foreach (var sub in GetSubdirectories(dir))
                stack.Push(sub);
        }

        return found.Values
            .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsGitRepository(string dir)
    {
        // A ".git" directory is the common case; a ".git" *file* covers worktrees/submodules.
        var gitEntry = Path.Combine(dir, ".git");
        return Directory.Exists(gitEntry) || File.Exists(gitEntry);
    }

    private static string GetFolderName(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? dir : name;
    }

    private static List<string> GetSubdirectories(string dir)
    {
        var result = new List<string>();

        string[] entries;
        try
        {
            // GetDirectories materializes immediately, so access errors surface here and are
            // caught, rather than during lazy enumeration later.
            entries = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                      or DirectoryNotFoundException
                                      or IOException
                                      or System.Security.SecurityException)
        {
            return result;
        }

        foreach (var sub in entries)
        {
            if (IgnoredDirectoryNames.Contains(Path.GetFileName(sub)))
                continue;

            try
            {
                // Skip symlinks/junctions to avoid cycles and escaping the intended tree.
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                    continue;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                                          or IOException
                                          or System.Security.SecurityException)
            {
                continue;
            }

            result.Add(sub);
        }

        return result;
    }
}
