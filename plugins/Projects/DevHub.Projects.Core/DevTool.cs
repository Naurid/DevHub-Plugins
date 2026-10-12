namespace DevHub.Projects.Core;

/// <summary>
/// A tool DevHub can open projects with. <see cref="ExecutablePath"/> is null for the
/// non-executable choices (OS default, Explorer, Custom), which the launcher handles specially.
/// </summary>
/// <param name="Id">One of the <see cref="ToolIds"/> constants.</param>
/// <param name="DisplayName">Label shown in Settings.</param>
/// <param name="ExecutablePath">Resolved exe path, or null for special/unresolved tools.</param>
public sealed record DevTool(string Id, string DisplayName, string? ExecutablePath);
