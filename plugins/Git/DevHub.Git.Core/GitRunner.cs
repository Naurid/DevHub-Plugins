using System.Diagnostics;
using System.Text;

namespace DevHub.Git.Core;

/// <summary>What a git command produced.</summary>
public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Per-command options for <see cref="GitRunner.RunAsync"/>.</summary>
public sealed record GitRunOptions
{
    /// <summary>Text written to git's stdin (e.g. a commit message for <c>commit -F -</c>).</summary>
    public string? StandardInput { get; init; }

    /// <summary>
    /// The command only reads. Sets GIT_OPTIONAL_LOCKS=0 so it never takes locks that would
    /// get in the way of git commands the user runs in a terminal at the same time.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>Return a failed result instead of throwing <see cref="GitException"/>.</summary>
    public bool AllowFailure { get; init; }

    /// <summary>
    /// Receives each stderr line while git runs (git writes progress there, redrawing a line
    /// with "\r"; both "\r" and "\n" end a line here). The full stderr is still in the result.
    /// Use with <c>--progress</c>, since git only prints progress to a terminal otherwise.
    /// </summary>
    public IProgress<string>? StandardErrorLines { get; init; }

    /// <summary>
    /// Allow helpers to interact with the user (don't set GIT_TERMINAL_PROMPT=0). Only for
    /// commands whose whole point is a sign-in window, like Git Credential Manager's
    /// <c>github login</c>; everything else must fail rather than wait for input.
    /// </summary>
    public bool Interactive { get; init; }

    /// <summary>
    /// The output may contain a secret (a token from <c>credential fill</c>): the Git Console
    /// records the command but not what it printed.
    /// </summary>
    public bool Sensitive { get; init; }

    /// <summary>Extra environment variables for this command only (null value = remove the variable).</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
}

/// <summary>A git command exited with a non-zero code.</summary>
public sealed class GitException(IReadOnlyList<string> arguments, int exitCode, string standardError)
    : Exception($"git {string.Join(' ', arguments)} failed ({exitCode}): {standardError.Trim()}")
{
    public IReadOnlyList<string> Arguments { get; } = arguments;
    public int ExitCode { get; } = exitCode;
    public string StandardError { get; } = standardError;
}

/// <summary>
/// The only way DevHub runs git (see docs/GIT_CLIENT_PLAN.md, "Rules"): one process per
/// command, arguments passed as a list (never a shell string), no interactive prompts, every
/// run recorded in the <see cref="IGitCommandLog"/>.
/// </summary>
public sealed class GitRunner
{
    private const int LoggedOutputLength = 2000;

    /// <summary>Added before every command: print paths as-is (UTF-8) instead of octal escapes.</summary>
    private static readonly string[] CommonOptions = ["-c", "core.quotepath=false"];

    private readonly GitExecutable _git;
    private readonly IGitCommandLog _log;
    private readonly IReadOnlyDictionary<string, string?> _environment;

    /// <param name="git">The git program to run.</param>
    /// <param name="log">Receives every command.</param>
    /// <param name="environment">
    /// Extra environment variables for every command (null value = remove the variable).
    /// Tests use it to isolate git from the user's own configuration.
    /// </param>
    public GitRunner(GitExecutable git, IGitCommandLog log, IReadOnlyDictionary<string, string?>? environment = null)
    {
        _git = git;
        _log = log;
        _environment = environment ?? new Dictionary<string, string?>();
    }

    /// <summary>
    /// Runs <c>git &lt;arguments&gt;</c> in <paramref name="workingDirectory"/> and waits for it.
    /// Throws <see cref="GitException"/> on a non-zero exit unless <see cref="GitRunOptions.AllowFailure"/>;
    /// throws <see cref="OperationCanceledException"/> (after killing git) when cancelled.
    /// </summary>
    public async Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        GitRunOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new GitRunOptions();

        var psi = new ProcessStartInfo(_git.Path)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Always redirected, and closed at once when there's nothing to send: a helper that
            // wants to ask something on the console (ssh's passphrase or host-key question) gets
            // "end of input" and fails, instead of waiting for an answer nobody can type.
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var option in CommonOptions)
            psi.ArgumentList.Add(option);
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        // Never wait for a password/username prompt nobody can see (unless the command is a sign-in).
        if (!options.Interactive)
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // English output, whatever the system language: some machine formats are translated
        // (for-each-ref's "ahead 2"/"gone"), and DevHub recognises a few error messages
        // ("would be overwritten by checkout"). LC_ALL wins over LANG for git's gettext.
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["LANGUAGE"] = "C";
        if (options.ReadOnly)
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var (name, value) in options.Environment is null ? _environment : _environment.Concat(options.Environment))
        {
            if (value is null)
                psi.Environment.Remove(name);
            else
                psi.Environment[name] = value;
        }

        var started = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.Start();

        // Start reading both streams before anything else: if git fills a pipe nobody reads
        // (stdout, stderr) while we're still writing stdin, both sides would wait forever.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = options.StandardErrorLines is { } lines
            ? ReadLinesAsync(process.StandardError, lines, ct)
            : process.StandardError.ReadToEndAsync(ct);

        try
        {
            if (options.StandardInput is not null)
            {
                // UTF-8 without BOM, so the first line of a commit message isn't polluted.
                await using var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false));
                await stdin.WriteAsync(options.StandardInput.AsMemory(), ct);
            }
            else
            {
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            _log.Add(new GitCommandEntry(workingDirectory, arguments, started, stopwatch.Elapsed, -1, "(cancelled)"));
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var result = new GitResult(process.ExitCode, stdout, stderr, stopwatch.Elapsed);

        var shown = options.Sensitive ? "(output hidden: it may contain a credential)"
            : string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        _log.Add(new GitCommandEntry(workingDirectory, arguments, started, result.Duration, result.ExitCode,
            shown.Length > LoggedOutputLength ? shown[..LoggedOutputLength] + "…" : shown,
            FailureExpected: options.AllowFailure));

        if (!result.Succeeded && !options.AllowFailure)
            throw new GitException(arguments, result.ExitCode, stderr);

        return result;
    }

    /// <summary>Reads a stream to the end, reporting each line ("\r" or "\n" ends one) as it arrives.</summary>
    private static async Task<string> ReadLinesAsync(StreamReader reader, IProgress<string> lines, CancellationToken ct)
    {
        var all = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[512];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            all.Append(buffer, 0, read);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] is '\r' or '\n')
                {
                    if (line.Length > 0)
                        lines.Report(line.ToString());
                    line.Clear();
                }
                else
                {
                    line.Append(buffer[i]);
                }
            }
        }
        if (line.Length > 0)
            lines.Report(line.ToString());
        return all.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill.
        }
    }
}
