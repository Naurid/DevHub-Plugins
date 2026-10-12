using System.Diagnostics;
using DevHub.Integration;

namespace DevHub.Services;

/// <summary>
/// The Git plugin's "Open in Editor" (F4 in diff / blame tabs, conflicts): a repository file in
/// the editor for the repository's language when the Projects plugin offers one
/// (<see cref="IProjectFileEditor"/>), else with the program Windows associates with the file.
/// Problems go to the status bar: callers are buttons.
/// </summary>
public sealed class RepositoryFileOpener(IStatusService status, IEnumerable<IProjectFileEditor> editors)
{
    private readonly IProjectFileEditor? _editor = editors.FirstOrDefault();

    /// <param name="repositoryPath">Repository root folder.</param>
    /// <param name="gitPath">The file as git names it (relative, forward slashes).</param>
    /// <param name="line">1-based line to jump to, or null (the Windows default can't jump to a line).</param>
    public void Open(string repositoryPath, string gitPath, int? line)
    {
        var fullPath = Path.GetFullPath(Path.Combine(repositoryPath, gitPath.Replace('/', Path.DirectorySeparatorChar)));
        if (_editor is not null)
        {
            _editor.OpenFile(repositoryPath, fullPath, line);
            return;
        }

        try
        {
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("The file doesn't exist (deleted?).", fullPath);
            // The file itself (not a command line), handed to Windows: no shell string involved.
            Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            status.Show($"Opened {gitPath}.");
        }
        catch (Exception ex)
        {
            status.Show($"Couldn't open {gitPath}: {ex.Message}");
        }
    }
}
