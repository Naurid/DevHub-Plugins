namespace DevHub.Projects.Core;

/// <summary>
/// The fallback tool for a language when the user hasn't set an override. Pure and testable:
/// given the ids of the tools actually installed, it picks the nicest available default.
/// </summary>
public static class DefaultToolPolicy
{
    public static string DefaultFor(string languageId, IReadOnlyCollection<string> availableToolIds)
    {
        string Prefer(string preferred) =>
            availableToolIds.Contains(preferred) ? preferred : ToolIds.OsDefault;

        return languageId switch
        {
            "unity" => Prefer(ToolIds.UnityHub),
            "dotnet" => Prefer(ToolIds.Rider),
            "tauri" or "rust" or "go" or "javascript" or "typescript" or "python" or "jvm" or "cpp"
                => Prefer(ToolIds.VsCode),
            _ => ToolIds.OsDefault
        };
    }
}
