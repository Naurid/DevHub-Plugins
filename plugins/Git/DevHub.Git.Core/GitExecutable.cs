using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DevHub.Git.Core;

/// <summary>
/// The git program DevHub runs: where it is and which version. Found once at startup;
/// <see cref="TryLocate"/> returns null when git isn't installed (or is too old), so the UI can
/// show "install Git" instead of failing on every command.
/// </summary>
public sealed partial class GitExecutable
{
    /// <summary>Oldest git DevHub supports (porcelain v2 status, modern switches).</summary>
    public static readonly Version MinimumVersion = new(2, 30);

    public GitExecutable(string path, Version version)
    {
        Path = path;
        Version = version;
    }

    /// <summary>Full path to git.exe.</summary>
    public string Path { get; }

    public Version Version { get; }

    /// <summary>
    /// Finds git on PATH (then the Git for Windows default folder) and checks its version.
    /// Null when not found, not runnable, or older than <see cref="MinimumVersion"/>.
    /// </summary>
    public static GitExecutable? TryLocate()
    {
        foreach (var candidate in Candidates())
        {
            if (!File.Exists(candidate))
                continue;

            var version = ReadVersion(candidate);
            if (version is not null && version >= MinimumVersion)
                return new GitExecutable(candidate, version);
        }

        return null;
    }

    /// <summary>
    /// Parses <c>git --version</c> output, e.g. "git version 2.47.1.windows.2" → 2.47.1.
    /// Returns null if the text doesn't look like a git version.
    /// </summary>
    public static Version? ParseVersion(string output)
    {
        var match = VersionPattern().Match(output);
        if (!match.Success)
            return null;

        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        return new Version(major, minor, patch);
    }

    private static IEnumerable<string> Candidates()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "git.exe" } : new[] { "git" };
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in pathDirs)
            foreach (var name in names)
                yield return System.IO.Path.Combine(dir.Trim('"'), name);

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return System.IO.Path.Combine(programFiles, "Git", "cmd", "git.exe");
        }
    }

    private static Version? ReadVersion(string gitPath)
    {
        try
        {
            var psi = new ProcessStartInfo(gitPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return ParseVersion(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"git version (\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionPattern();
}
