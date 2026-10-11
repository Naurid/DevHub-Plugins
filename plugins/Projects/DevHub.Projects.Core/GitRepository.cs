using DevHub.Projects.Core;

namespace DevHub.Projects.Core;

/// <summary>
/// A git repository discovered on disk. Carries identity (folder name), location, and the
/// detected primary language; branch/status and actions arrive in later milestones.
/// </summary>
/// <param name="Name">The repository folder's name (e.g. "DevHub").</param>
/// <param name="Path">The absolute path to the repository root (the folder holding .git).</param>
/// <param name="Language">The detected primary language, or <see cref="ProjectLanguage.Unknown"/>.</param>
public sealed record GitRepository(string Name, string Path, ProjectLanguage Language);
