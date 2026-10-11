using System.Diagnostics;

namespace DevHub.Git.Core;

/// <summary>A terminal to start: program, arguments (passed as a list, never one string) and folder.</summary>
public sealed record TerminalCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    /// <summary>"Git Bash", "Windows Terminal", "PowerShell": for the status bar.</summary>
    public required string Name { get; init; }
}

/// <summary>
/// "Open Terminal" for a repository (Fork's "Open in Terminal"): a shell in the repository's
/// folder for anything DevHub doesn't do itself. Prefers Git Bash (part of Git for Windows, so
/// git and its tools are on the PATH), then Windows Terminal, then PowerShell.
/// </summary>
public static class GitTerminal
{
    /// <summary>Picks the terminal (pure: the file checks are passed in, so it's testable).</summary>
    /// <param name="gitPath">git.exe in use (Git Bash sits in the same installation), or null.</param>
    /// <param name="fileExists">File.Exists, or a fake in tests.</param>
    /// <param name="findOnPath">Full path of a program found on the PATH (or in WindowsApps), or null.</param>
    public static TerminalCommand Choose(string repositoryPath, string? gitPath, Func<string, bool> fileExists,
        Func<string, string?> findOnPath)
    {
        if (GitBashFor(gitPath, fileExists) is { } bash)
            return new TerminalCommand(bash, ["--cd=" + repositoryPath], repositoryPath) { Name = "Git Bash" };
        if (findOnPath("wt.exe") is { } wt)
            return new TerminalCommand(wt, ["-d", repositoryPath], repositoryPath) { Name = "Windows Terminal" };
        return new TerminalCommand(findOnPath("powershell.exe") ?? "powershell.exe", ["-NoExit"], repositoryPath)
        {
            Name = "PowerShell",
        };
    }

    /// <summary>
    /// git-bash.exe of the installation git.exe belongs to: git.exe lives in "cmd", "bin" or
    /// "mingw64\bin" under the installation folder, and git-bash.exe at its top.
    /// </summary>
    public static string? GitBashFor(string? gitPath, Func<string, bool> fileExists)
    {
        var dir = gitPath is null ? null : Path.GetDirectoryName(gitPath);
        for (var i = 0; i < 3 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var bash = Path.Combine(dir, "git-bash.exe");
            if (fileExists(bash))
                return bash;
        }
        return null;
    }

    /// <summary>Finds a program on the PATH (Windows Terminal is found there through WindowsApps).</summary>
    public static string? FindOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(dir => Path.Combine(dir.Trim('"'), program))
        .FirstOrDefault(File.Exists);

    /// <summary>Starts the terminal in its own window. Throws when it can't be started.</summary>
    public static void Start(TerminalCommand command)
    {
        var psi = new ProcessStartInfo(command.FileName)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in command.Arguments)
            psi.ArgumentList.Add(argument);
        Process.Start(psi)?.Dispose();
    }
}
