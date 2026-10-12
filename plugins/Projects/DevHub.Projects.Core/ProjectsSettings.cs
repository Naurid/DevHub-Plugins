using DevHub.Sdk.Core.Settings;

namespace DevHub.Projects.Core;

/// <summary>
/// The Projects plugin's settings section: which folders to scan, and the per-language "open
/// with" overrides. Only user overrides are stored: unset languages fall back to
/// <see cref="DefaultToolPolicy"/>.
/// </summary>
public sealed class ProjectsSettings : ISettingsSection
{
    public static string SectionId => "devhub.projects";

    /// <summary>Folders the Projects tool window scans for git repositories.</summary>
    public List<string> RepositoryFolders { get; set; } = new();

    /// <summary>Language id → chosen tool. Missing keys mean "use the default policy".</summary>
    public Dictionary<string, LanguageToolChoice> LanguageTools { get; set; } = new();
}
