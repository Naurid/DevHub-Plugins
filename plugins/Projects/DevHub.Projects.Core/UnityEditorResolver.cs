namespace DevHub.Projects.Core;

/// <summary>
/// Resolves the exact Unity editor a project pins. Unity writes its editor version to
/// <c>ProjectSettings/ProjectVersion.txt</c> (<c>m_EditorVersion: 6000.3.13f1</c>); Hub installs
/// editors under <c>&lt;editorsRoot&gt;/&lt;version&gt;/Editor/Unity.exe</c>.
/// </summary>
public static class UnityEditorResolver
{
    /// <summary>The default Hub editors install root.</summary>
    public static string DefaultEditorsRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Unity", "Hub", "Editor");

    /// <summary>
    /// Returns the path to the matching <c>Unity.exe</c> for the project, or null if the version
    /// can't be read or that editor isn't installed (caller should fall back to Unity Hub).
    /// </summary>
    public static string? Resolve(string repoPath, string editorsRoot)
    {
        var version = ReadEditorVersion(repoPath);
        if (version is null)
            return null;

        var exe = Path.Combine(editorsRoot, version, "Editor", "Unity.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>Parses <c>m_EditorVersion:</c> from the project's ProjectVersion.txt.</summary>
    public static string? ReadEditorVersion(string repoPath)
    {
        var file = Path.Combine(repoPath, "ProjectSettings", "ProjectVersion.txt");
        try
        {
            if (!File.Exists(file))
                return null;

            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                {
                    var value = trimmed["m_EditorVersion:".Length..].Trim();
                    return value.Length > 0 ? value : null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
