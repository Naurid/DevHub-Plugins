namespace DevHub.Projects.Core;

/// <summary>
/// Determines a repository's primary language from the files at its root.
/// </summary>
public interface IProjectLanguageDetector
{
    /// <summary>
    /// Inspects <paramref name="repoPath"/> and returns the detected language, or
    /// <see cref="ProjectLanguage.Unknown"/> if nothing matches.
    /// </summary>
    ProjectLanguage Detect(string repoPath);
}
