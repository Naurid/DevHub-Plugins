namespace DevHub.Projects.Core;

/// <summary>
/// Windows-focused tool detection. Probes well-known install locations once and caches the
/// result. The "special" tools (OS default, Explorer, Custom) are always offered.
/// </summary>
public sealed class ToolLocator : IToolLocator
{
    private List<DevTool>? _cache;

    public IReadOnlyList<DevTool> GetAvailableTools() => _cache ??= Detect();

    public DevTool? Find(string toolId) =>
        GetAvailableTools().FirstOrDefault(t => t.Id == toolId);

    private static List<DevTool> Detect()
    {
        var tools = new List<DevTool>
        {
            new(ToolIds.OsDefault, "OS default", null),
            new(ToolIds.Explorer, "File Explorer", null)
        };

        AddIfFound(tools, ToolIds.Rider, "Rider", FindRider());
        AddIfFound(tools, ToolIds.VsCode, "VS Code", FindVsCode());
        AddIfFound(tools, ToolIds.VisualStudio, "Visual Studio", FindVisualStudio());
        AddIfFound(tools, ToolIds.UnityHub, "Unity Hub", FindUnityHub());

        // Custom is always an option; its executable comes from the per-language setting.
        tools.Add(new DevTool(ToolIds.Custom, "Custom…", null));
        return tools;
    }

    private static void AddIfFound(List<DevTool> tools, string id, string name, string? exe)
    {
        if (exe is not null)
            tools.Add(new DevTool(id, name, exe));
    }

    private static string? FindRider()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var fixedCandidates = new[]
        {
            Path.Combine(local, "Programs", "Rider", "bin", "rider64.exe")
        };
        var found = FirstExisting(fixedCandidates);
        if (found is not null)
            return found;

        // JetBrains Toolbox installs: …\JetBrains\Toolbox\apps\Rider\<channel>\<build>\bin\rider64.exe
        var toolboxRider = Path.Combine(local, "JetBrains", "Toolbox", "apps", "Rider");
        found = DeepFind(toolboxRider, "rider64.exe", maxDepth: 4);
        if (found is not null)
            return found;

        // Standalone install: %ProgramFiles%\JetBrains\JetBrains Rider <ver>\bin\rider64.exe
        return DeepFind(Path.Combine(programFiles, "JetBrains"), "rider64.exe", maxDepth: 3);
    }

    private static string? FindVsCode()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var candidates = new[]
        {
            Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(programFiles, "Microsoft VS Code", "Code.exe")
        };
        var found = FirstExisting(candidates);
        if (found is not null)
            return found;

        // Fall back to the code.cmd shim on PATH, deriving Code.exe next to its bin folder.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            if (File.Exists(Path.Combine(dir, "code.cmd")))
            {
                var exe = Path.Combine(Path.GetDirectoryName(dir) ?? dir, "Code.exe");
                if (File.Exists(exe))
                    return exe;
            }
        }

        return null;
    }

    private static string? FindVisualStudio()
    {
        foreach (var pf in ProgramFilesRoots())
        {
            // …\Microsoft Visual Studio\<year>\<edition>\Common7\IDE\devenv.exe
            var vsRoot = Path.Combine(pf, "Microsoft Visual Studio");
            foreach (var year in SafeDirs(vsRoot))
            foreach (var edition in SafeDirs(year))
            {
                var exe = Path.Combine(edition, "Common7", "IDE", "devenv.exe");
                if (File.Exists(exe))
                    return exe;
            }
        }
        return null;
    }

    private static string? FindUnityHub()
    {
        foreach (var pf in ProgramFilesRoots())
        {
            var exe = Path.Combine(pf, "Unity Hub", "Unity Hub.exe");
            if (File.Exists(exe))
                return exe;
        }
        return null;
    }

    private static IEnumerable<string> ProgramFilesRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    }

    private static string? FirstExisting(IEnumerable<string> candidates) =>
        candidates.FirstOrDefault(File.Exists);

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path) : []; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return []; }
    }

    /// <summary>Breadth-limited search for a file name under a root (handles versioned folders).</summary>
    private static string? DeepFind(string root, string fileName, int maxDepth)
    {
        if (!Directory.Exists(root))
            return null;

        var queue = new Queue<(string dir, int depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            var hit = Path.Combine(dir, fileName);
            if (File.Exists(hit))
                return hit;

            if (depth >= maxDepth)
                continue;
            foreach (var sub in SafeDirs(dir))
                queue.Enqueue((sub, depth + 1));
        }
        return null;
    }
}
