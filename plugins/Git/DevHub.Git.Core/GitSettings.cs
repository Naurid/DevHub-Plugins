using DevHub.Sdk.Core.Settings;

namespace DevHub.Git.Core;

/// <summary>Git client view preferences (all repository tabs share them). The Git plugin's settings section.</summary>
public sealed class GitSettings : ISettingsSection
{
    public static string SectionId => "devhub.git";

    /// <summary>Local Changes shows folders (Fork's tree view) rather than a flat file list.</summary>
    public bool ChangesAsTree { get; set; } = true;

    /// <summary>Diffs show old and new side by side rather than unified (JetBrains' viewer switch).</summary>
    public bool DiffSideBySide { get; set; }
}
