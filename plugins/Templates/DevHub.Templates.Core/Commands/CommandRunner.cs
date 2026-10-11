using System.Diagnostics;
using System.Text;
using DevHub.Templates.Core.Rendering;

namespace DevHub.Templates.Core.Commands;

/// <summary>One line a command printed (stderr lines marked), for the live output log.</summary>
public sealed record CommandOutputLine(string Command, string Text, bool IsError);

/// <summary>How a command ended.</summary>
/// <param name="ExitCode">The process exit code; null when it couldn't start.</param>
/// <param name="Error">Why it failed in plain words (couldn't start, non-zero exit), or null.</param>
/// <param name="Output">Everything it printed, in order (last lines show in the error view).</param>
public sealed record CommandResult(RenderedCommand Command, int? ExitCode, string? Error, IReadOnlyList<CommandOutputLine> Output)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Runs a template's commands (base command, post-create). Never through a shell string:
/// <c>UseShellExecute = false</c>, each argument in <c>ArgumentList</c>. On Windows the command is
/// looked up on PATH with PATHEXT, and npm / npx / yarn-style <c>.cmd</c> / <c>.bat</c> shims run
/// through <c>cmd.exe /c</c> (still with separate arguments). <c>@name</c> is a DevHub tool alias
/// (e.g. <c>@unity</c>), resolved by the app. Output streams line by line; cancelling kills the
/// whole process tree.
/// </summary>
public sealed class CommandRunner
{
    private readonly Func<string, string?> _resolveAlias;
    private readonly Func<string?> _path;

    /// <param name="resolveAlias">"unity" → the Unity editor's path (null when unknown); null = no aliases.</param>
    /// <param name="path">The PATH to search (tests pass their own); null = the process's PATH.</param>
    public CommandRunner(Func<string, string?>? resolveAlias = null, Func<string?>? path = null)
    {
        _resolveAlias = resolveAlias ?? (_ => null);
        _path = path ?? (() => Environment.GetEnvironmentVariable("PATH"));
    }

    /// <param name="workingDirectory">Absolute folder to run in.</param>
    public async Task<CommandResult> RunAsync(RenderedCommand command, string workingDirectory,
        IProgress<CommandOutputLine>? progress = null, CancellationToken ct = default)
    {
        var output = new List<CommandOutputLine>();
        if (Resolve(command.Command) is not { } executable)
        {
            var what = command.Command.StartsWith('@')
                ? $"DevHub doesn't know where {command.Command[1..]} is installed."
                : $"\"{command.Command}\" wasn't found. Is it installed and on PATH?";
            return new CommandResult(command, null, what, output);
        }
        if (!Directory.Exists(workingDirectory))
            return new CommandResult(command, null, $"The folder {workingDirectory} doesn't exist.", output);

        var info = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // closed right away: a command waiting for input gets EOF, not a hang
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (IsBatchFile(executable))
        {
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(executable);
        }
        else
        {
            info.FileName = executable;
        }
        foreach (var arg in command.Args)
            info.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var gate = new object();
        void OnLine(string? text, bool isError)
        {
            if (text is null)
                return;
            var line = new CommandOutputLine(command.Name, text, isError);
            lock (gate)
                output.Add(line);
            progress?.Report(line);
        }
        process.OutputDataReceived += (_, e) => OnLine(e.Data, false);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data, true);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CommandResult(command, null, $"Couldn't start {command.Command}: {ex.Message}", output);
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        process.WaitForExit(); // flushes the remaining output events

        List<CommandOutputLine> lines;
        lock (gate)
            lines = [.. output];
        var error = process.ExitCode == 0 ? null : $"{command.Name} failed (exit code {process.ExitCode}).";
        return new CommandResult(command, process.ExitCode, error, lines);
    }

    /// <summary>The executable to start: an alias, a path, or a name found on PATH (with PATHEXT on Windows); null when not found.</summary>
    public string? Resolve(string command)
    {
        if (command.StartsWith('@'))
            return _resolveAlias(command[1..]) is { } aliased && File.Exists(aliased) ? aliased : null;

        if (Path.IsPathRooted(command) || command.Contains('/') || command.Contains('\\'))
            return File.Exists(command) ? Path.GetFullPath(command) : null;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        var hasExtension = Path.HasExtension(command);
        foreach (var folder in (_path() ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (hasExtension && File.Exists(Path.Combine(folder, command)))
                return Path.Combine(folder, command);
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder.Trim('"'), command + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static bool IsBatchFile(string path) =>
        OperatingSystem.IsWindows() && Path.GetExtension(path).ToLowerInvariant() is ".cmd" or ".bat";
}
