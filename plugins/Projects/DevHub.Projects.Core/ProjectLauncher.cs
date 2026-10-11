using System.Diagnostics;
using DevHub.Projects.Core;
using DevHub.Sdk.Core.Settings;

namespace DevHub.Projects.Core;

/// <summary>A fully-resolved command to run: executable, arguments, and launch mode.</summary>
/// <param name="FileName">Executable (or file to shell-open).</param>
/// <param name="Arguments">Arguments, each already a separate token (no shell quoting needed).</param>
/// <param name="UseShellExecute">
/// true = hand the file to the OS default handler (e.g. open a .sln in the user's IDE);
/// false = run the executable directly.
/// </param>
public sealed record LaunchSpec(string FileName, IReadOnlyList<string> Arguments, bool UseShellExecute);

/// <summary>Detected tool paths + Unity editors root, passed to the pure <see cref="ProjectLauncher.Resolve"/>.</summary>
public sealed record ToolContext(
    string? RiderPath,
    string? VsCodePath,
    string? DevenvPath,
    string? UnityHubPath,
    string UnityEditorsRoot);

/// <summary>
/// Opens a repository with the tool configured for its language. The decision
/// (<see cref="Resolve"/>) is pure and unit-tested; starting the process is a thin wrapper.
/// </summary>
public sealed class ProjectLauncher : IProjectLauncher
{
    private readonly ISettingsStore _settings;
    private readonly IToolLocator _tools;

    public ProjectLauncher(ISettingsStore settings, IToolLocator tools)
    {
        _settings = settings;
        _tools = tools;
    }

    public void Open(GitRepository repo)
    {
        var choice = EffectiveChoice(repo.Language.Id);
        var spec = Resolve(repo, choice, BuildContext());
        Start(spec);
    }

    public void OpenFile(GitRepository repo, string filePath, int? line = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("The file doesn't exist (deleted?).", filePath);

        var choice = EffectiveChoice(repo.Language.Id);
        Start(ResolveFile(repo, filePath, line, choice, BuildContext()));
    }

    public void RevealInFileManager(GitRepository repo) =>
        Start(new LaunchSpec("explorer.exe", new[] { repo.Path }, UseShellExecute: true));

    /// <summary>The user's override for a language, or the default-policy choice if none.</summary>
    private LanguageToolChoice EffectiveChoice(string languageId)
    {
        if (_settings.LoadSection<ProjectsSettings>().LanguageTools.TryGetValue(languageId, out var choice))
            return choice;

        var available = _tools.GetAvailableTools().Select(t => t.Id).ToList();
        return new LanguageToolChoice { ToolId = DefaultToolPolicy.DefaultFor(languageId, available) };
    }

    private ToolContext BuildContext() => new(
        RiderPath: _tools.Find(ToolIds.Rider)?.ExecutablePath,
        VsCodePath: _tools.Find(ToolIds.VsCode)?.ExecutablePath,
        DevenvPath: _tools.Find(ToolIds.VisualStudio)?.ExecutablePath,
        UnityHubPath: _tools.Find(ToolIds.UnityHub)?.ExecutablePath,
        UnityEditorsRoot: UnityEditorResolver.DefaultEditorsRoot);

    /// <summary>
    /// Pure resolution of how to open <paramref name="repo"/> given the chosen tool. Falls back
    /// to opening the folder (shell) when a chosen tool isn't available.
    /// </summary>
    public static LaunchSpec Resolve(GitRepository repo, LanguageToolChoice choice, ToolContext ctx)
    {
        var folder = repo.Path;

        switch (choice.ToolId)
        {
            case ToolIds.Explorer:
                return new LaunchSpec("explorer.exe", new[] { folder }, UseShellExecute: true);

            case ToolIds.VsCode when ctx.VsCodePath is not null:
                return Exe(ctx.VsCodePath, folder);

            case ToolIds.Rider when ctx.RiderPath is not null:
                return Exe(ctx.RiderPath, folder);

            case ToolIds.VisualStudio when ctx.DevenvPath is not null:
                var sln = FindFirst(folder, "*.sln");
                return sln is not null ? Exe(ctx.DevenvPath, sln) : ShellOpenFolder(folder);

            case ToolIds.UnityHub:
                return ResolveUnity(folder, ctx);

            case ToolIds.Custom when !string.IsNullOrWhiteSpace(choice.CustomExe):
                return ResolveCustom(choice, repo);

            case ToolIds.OsDefault:
                return ResolveOsDefault(folder);

            default:
                // Chosen tool not available (or custom without an exe) → safe fallback.
                return ResolveOsDefault(folder);
        }
    }

    /// <summary>
    /// Pure resolution of how to open one file of <paramref name="repo"/> with the chosen tool,
    /// at <paramref name="line"/> where the tool's command line supports it (Rider, VS Code).
    /// Unity isn't a code editor, so Unity repos use Rider, then VS Code, then the file's OS
    /// default app. A chosen tool that isn't installed also falls back to the OS default.
    /// </summary>
    public static LaunchSpec ResolveFile(GitRepository repo, string filePath, int? line,
        LanguageToolChoice choice, ToolContext ctx)
    {
        switch (choice.ToolId)
        {
            case ToolIds.Explorer:
                // explorer.exe /select, "<path>": the folder with the file selected.
                return new LaunchSpec("explorer.exe", ["/select,", filePath], UseShellExecute: false);

            case ToolIds.Rider when ctx.RiderPath is not null:
                return RiderFile(ctx.RiderPath, filePath, line);

            case ToolIds.VsCode when ctx.VsCodePath is not null:
                return VsCodeFile(ctx.VsCodePath, repo.Path, filePath, line);

            case ToolIds.VisualStudio when ctx.DevenvPath is not null:
                // /Edit opens the file in a running Visual Studio (or starts one); no line option.
                return new LaunchSpec(ctx.DevenvPath, ["/Edit", filePath], UseShellExecute: true);

            case ToolIds.UnityHub when ctx.RiderPath is not null:
                return RiderFile(ctx.RiderPath, filePath, line);

            case ToolIds.UnityHub when ctx.VsCodePath is not null:
                return VsCodeFile(ctx.VsCodePath, repo.Path, filePath, line);

            case ToolIds.Custom when !string.IsNullOrWhiteSpace(choice.CustomExe):
                return Exe(choice.CustomExe!, filePath);

            default:
                // OS default, or the chosen tool isn't installed: the file's default app.
                return new LaunchSpec(filePath, [], UseShellExecute: true);
        }
    }

    // rider64.exe --line 12 <file>: opens the file (in the open solution if it belongs to one).
    private static LaunchSpec RiderFile(string rider, string filePath, int? line) =>
        line is { } n
            ? new LaunchSpec(rider, ["--line", n.ToString(System.Globalization.CultureInfo.InvariantCulture), filePath], UseShellExecute: true)
            : Exe(rider, filePath);

    // code <repo> -g <file>:12: reuses the repository's window (or opens it) and jumps to the line.
    private static LaunchSpec VsCodeFile(string code, string folder, string filePath, int? line) =>
        new(code, [folder, "-g", line is { } n ? $"{filePath}:{n}" : filePath], UseShellExecute: true);

    private static LaunchSpec ResolveOsDefault(string folder)
    {
        var projectFile = FindFirst(folder, "*.sln") ?? FindFirst(folder, "*.csproj");
        return projectFile is not null
            ? new LaunchSpec(projectFile, Array.Empty<string>(), UseShellExecute: true)
            : ShellOpenFolder(folder);
    }

    private static LaunchSpec ResolveUnity(string folder, ToolContext ctx)
    {
        var editor = UnityEditorResolver.Resolve(folder, ctx.UnityEditorsRoot);
        if (editor is not null)
            return new LaunchSpec(editor, new[] { "-projectPath", folder }, UseShellExecute: true);

        if (ctx.UnityHubPath is not null)
            return new LaunchSpec(ctx.UnityHubPath, new[] { "--", "--projectPath", folder }, UseShellExecute: true);

        return ShellOpenFolder(folder);
    }

    private static LaunchSpec ResolveCustom(LanguageToolChoice choice, GitRepository repo) =>
        Exe(choice.CustomExe!, repo.Path); // custom tool is launched on the repo folder

    // GUI tools are launched detached (UseShellExecute = true) so they own their own
    // stdout/stderr — otherwise the child inherits DevHub's streams and its console logging
    // leaks into our logs.
    private static LaunchSpec Exe(string exe, string arg) =>
        new(exe, new[] { arg }, UseShellExecute: true);

    private static LaunchSpec ShellOpenFolder(string folder) =>
        new(folder, Array.Empty<string>(), UseShellExecute: true);

    private static string? FindFirst(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return null;
        }
    }

    private static void Start(LaunchSpec spec)
    {
        var psi = new ProcessStartInfo(spec.FileName) { UseShellExecute = spec.UseShellExecute };
        foreach (var arg in spec.Arguments)
            psi.ArgumentList.Add(arg);
        Process.Start(psi);
    }
}
