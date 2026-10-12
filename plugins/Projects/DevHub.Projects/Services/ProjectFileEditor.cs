using DevHub.Integration;
using DevHub.Projects.Core;

namespace DevHub.Services;

/// <summary>
/// The Projects plugin's <see cref="IProjectFileEditor"/>: opens a file in the external editor
/// tied to its project's language (Settings → Open With), e.g. for Git's "Open in Editor" (F4).
/// Problems go to the status bar instead of throwing, since callers are buttons.
/// </summary>
public sealed class ProjectFileEditor(IProjectLauncher launcher, IProjectLanguageDetector detector, IStatusService status)
    : IProjectFileEditor
{
    public void OpenFile(string projectFolder, string filePath, int? line)
    {
        var name = Path.GetFileName(projectFolder.TrimEnd('\\', '/'));
        var project = new GitRepository(name, projectFolder, detector.Detect(projectFolder));
        var shown = Path.GetRelativePath(projectFolder, filePath);
        try
        {
            launcher.OpenFile(project, filePath, line);
            status.Show($"Opened {shown}.");
        }
        catch (Exception ex)
        {
            status.Show($"Couldn't open {shown}: {ex.Message}");
        }
    }
}
