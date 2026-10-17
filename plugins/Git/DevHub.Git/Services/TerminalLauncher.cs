using DevHub.Git.Core;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Services;

/// <summary>
/// Opens a terminal in a repository folder (Git Bash, else Windows Terminal, else PowerShell;
/// <see cref="GitTerminal"/> picks), for git commands DevHub doesn't offer. Problems go to the
/// status bar: callers are buttons.
/// </summary>
/// <param name="git">The git in use (its Git Bash is preferred); null when git isn't installed.</param>
public sealed class TerminalLauncher(IStatusService status, GitExecutable? git = null)
{
    public void Open(string repositoryPath)
    {
        var command = GitTerminal.Choose(repositoryPath, git?.Path, File.Exists, GitTerminal.FindOnPath);
        try
        {
            GitTerminal.Start(command);
            status.Show(L.T($"Opened {command.Name} in {repositoryPath}."));
        }
        catch (Exception ex)
        {
            status.Show(L.T($"Couldn't open {command.Name}: {ex.Message}"));
        }
    }
}
