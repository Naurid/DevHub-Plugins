namespace DevHub.Projects.Core;

/// <summary>
/// A user's choice of which tool opens a given language. <see cref="ToolId"/> is one of the
/// <see cref="Core.ToolIds"/> constants; the Custom* fields apply only when it is "custom".
/// Mutable with parameterless ctor so System.Text.Json round-trips it plainly.
/// </summary>
public sealed class LanguageToolChoice
{
    public string ToolId { get; set; } = ToolIds.OsDefault;

    /// <summary>Executable to run when <see cref="ToolId"/> is "custom"; opened on the repo folder.</summary>
    public string? CustomExe { get; set; }
}
