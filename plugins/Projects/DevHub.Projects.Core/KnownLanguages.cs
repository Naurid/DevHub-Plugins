namespace DevHub.Projects.Core;

/// <summary>
/// The canonical set of languages DevHub recognises. Single source of truth for both
/// <see cref="ProjectLanguageDetector"/> and the Settings "Open Tools" rows.
/// </summary>
public static class KnownLanguages
{
    public static readonly ProjectLanguage Unity = new("unity", "Unity");
    public static readonly ProjectLanguage DotNet = new("dotnet", "C#");
    public static readonly ProjectLanguage Rust = new("rust", "Rust");
    public static readonly ProjectLanguage Go = new("go", "Go");
    public static readonly ProjectLanguage Tauri = new("tauri", "Tauri");
    public static readonly ProjectLanguage JavaScript = new("javascript", "JavaScript");
    public static readonly ProjectLanguage TypeScript = new("typescript", "TypeScript");
    public static readonly ProjectLanguage Python = new("python", "Python");
    public static readonly ProjectLanguage Jvm = new("jvm", "Java/Kotlin");
    public static readonly ProjectLanguage Cpp = new("cpp", "C/C++");

    /// <summary>All recognised languages, in the order shown in Settings.</summary>
    public static readonly IReadOnlyList<ProjectLanguage> All =
    [
        Unity, DotNet, Tauri, Rust, Go, JavaScript, TypeScript, Python, Jvm, Cpp
    ];
}
