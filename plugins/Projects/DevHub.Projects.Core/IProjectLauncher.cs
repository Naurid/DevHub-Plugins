using DevHub.Projects.Core;

namespace DevHub.Projects.Core;

/// <summary>
/// Opens a repository with the appropriate tool, or reveals it in the OS file manager.
/// </summary>
public interface IProjectLauncher
{
    /// <summary>
    /// Opens the repo with the "right tool": a solution/project file via its OS association
    /// (e.g. a .sln in the user's default IDE), otherwise the folder in VS Code, otherwise the
    /// file manager. Throws if the chosen target can't be launched.
    /// </summary>
    void Open(GitRepository repo);

    /// <summary>
    /// Opens one file of the repo in the editor chosen for the repo's language (Settings →
    /// Open With), at <paramref name="line"/> when the tool supports it. Throws if the file
    /// doesn't exist or the tool can't be launched.
    /// </summary>
    /// <param name="filePath">Absolute path of the file.</param>
    /// <param name="line">1-based line to jump to, or null.</param>
    void OpenFile(GitRepository repo, string filePath, int? line = null);

    /// <summary>Reveals the repo folder in the OS file manager (always available).</summary>
    void RevealInFileManager(GitRepository repo);
}
