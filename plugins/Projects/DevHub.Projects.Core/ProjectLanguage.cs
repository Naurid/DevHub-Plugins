namespace DevHub.Projects.Core;

/// <summary>
/// The primary language/ecosystem a repository is built in, as detected from marker files.
/// </summary>
/// <param name="Id">Stable lowercase key (e.g. "dotnet", "python") — safe for mapping/config.</param>
/// <param name="DisplayName">Human-readable label shown in the UI (e.g. "C#", "Python").</param>
public sealed record ProjectLanguage(string Id, string DisplayName)
{
    /// <summary>Used when no marker file matches.</summary>
    public static readonly ProjectLanguage Unknown = new("unknown", "Unknown");
}
