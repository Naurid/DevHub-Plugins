namespace DevHub.Diagrams.Core;

/// <summary>
/// The link between a diagram and its project (<see cref="Diagram.Project"/>): stored relative to the diagram file,
/// so moving or cloning the whole project keeps it. New diagrams of a project go to <see cref="DiagramsFolder"/>.
/// </summary>
public static class ProjectLinks
{
    /// <summary>Where a project's new diagrams go, relative to the project folder.</summary>
    public static readonly string DiagramsFolder = Path.Combine("docs", "diagrams");

    /// <summary>The project folder as stored in the file ("../.."), from the diagram's path and the project's folder.</summary>
    public static string Relative(string diagramPath, string projectFolder)
    {
        var from = Path.GetDirectoryName(Path.GetFullPath(diagramPath))!;
        var relative = Path.GetRelativePath(from, Path.GetFullPath(projectFolder));
        return relative.Replace('\\', '/');
    }

    /// <summary>The project folder of a diagram, or null when it isn't linked.</summary>
    public static string? Resolve(string diagramPath, Diagram diagram)
    {
        if (string.IsNullOrWhiteSpace(diagram.Project))
            return null;
        var from = Path.GetDirectoryName(Path.GetFullPath(diagramPath))!;
        var folder = Path.GetFullPath(Path.Combine(from, diagram.Project.Replace('/', Path.DirectorySeparatorChar)));
        return folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>Whether a diagram is linked to a project folder (paths compared case-insensitively on Windows).</summary>
    public static bool BelongsTo(string diagramPath, Diagram diagram, string projectFolder)
    {
        var resolved = Resolve(diagramPath, diagram);
        if (resolved is null)
            return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(resolved, Path.GetFullPath(projectFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison);
    }

    /// <summary>
    /// The diagram files of a project: every <c>*.devhub-diagram.json</c> under its folder (skipping build output,
    /// dependencies and hidden folders). Diagrams outside the folder that link to it are found by the caller's list.
    /// </summary>
    public static IReadOnlyList<string> FilesUnder(string projectFolder, int maxDepth = 6)
    {
        var result = new List<string>();
        if (!Directory.Exists(projectFolder))
            return result;
        void Walk(string folder, int depth)
        {
            try
            {
                result.AddRange(Directory.EnumerateFiles(folder, "*" + DiagramFile.Extension));
                if (depth >= maxDepth)
                    return;
                foreach (var sub in Directory.EnumerateDirectories(folder))
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules" or "packages" or "target" or "dist" or "build")
                        continue;
                    Walk(sub, depth + 1);
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // A folder we can't read has no diagrams for us.
            }
        }
        Walk(projectFolder, 0);
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}
