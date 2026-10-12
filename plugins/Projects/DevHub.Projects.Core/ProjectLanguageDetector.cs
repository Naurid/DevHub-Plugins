namespace DevHub.Projects.Core;

/// <summary>
/// Marker-file based language detection: looks only at the repository root and applies an
/// ordered rule set, first match wins. Cheap and deterministic — no file contents are read.
/// </summary>
public sealed class ProjectLanguageDetector : IProjectLanguageDetector
{
    public ProjectLanguage Detect(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return ProjectLanguage.Unknown;

        // Unity before C#: a Unity project also contains generated .csproj files, so the more
        // specific signal must win.
        if (HasDir(repoPath, "Assets") && HasDir(repoPath, "ProjectSettings"))
            return KnownLanguages.Unity;

        if (HasPattern(repoPath, "*.sln") || HasPattern(repoPath, "*.csproj") || HasPattern(repoPath, "*.fsproj"))
            return KnownLanguages.DotNet;

        if (HasFile(repoPath, "Cargo.toml"))
            return KnownLanguages.Rust;

        if (HasFile(repoPath, "go.mod"))
            return KnownLanguages.Go;

        // Tauri before Node: a Tauri repo has a JS/TS frontend (package.json) plus a src-tauri/
        // Rust backend, so the more specific src-tauri signal must win.
        if (HasDir(repoPath, "src-tauri"))
            return KnownLanguages.Tauri;

        if (HasFile(repoPath, "package.json"))
        {
            return HasFile(repoPath, "tsconfig.json")
                ? KnownLanguages.TypeScript
                : KnownLanguages.JavaScript;
        }

        if (HasFile(repoPath, "pyproject.toml") || HasFile(repoPath, "requirements.txt")
            || HasFile(repoPath, "setup.py") || HasFile(repoPath, "Pipfile"))
            return KnownLanguages.Python;

        if (HasFile(repoPath, "pom.xml") || HasFile(repoPath, "build.gradle")
            || HasFile(repoPath, "build.gradle.kts"))
            return KnownLanguages.Jvm;

        if (HasFile(repoPath, "CMakeLists.txt") || HasPattern(repoPath, "*.vcxproj"))
            return KnownLanguages.Cpp;

        return ProjectLanguage.Unknown;
    }

    private static bool HasDir(string root, string name) =>
        Directory.Exists(Path.Combine(root, name));

    private static bool HasFile(string root, string name) =>
        File.Exists(Path.Combine(root, name));

    private static bool HasPattern(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                      or DirectoryNotFoundException
                                      or IOException)
        {
            return false;
        }
    }
}
