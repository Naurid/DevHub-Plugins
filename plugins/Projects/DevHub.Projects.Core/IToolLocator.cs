namespace DevHub.Projects.Core;

/// <summary>
/// Discovers which dev tools are installed and where. Used to populate the Settings dropdowns
/// and to resolve executable paths when launching.
/// </summary>
public interface IToolLocator
{
    /// <summary>
    /// The tools offerable per language: the always-available choices (OS default, Explorer,
    /// Custom) plus every detected IDE/editor. Order is UI-friendly.
    /// </summary>
    IReadOnlyList<DevTool> GetAvailableTools();

    /// <summary>The tool for <paramref name="toolId"/>, or null if it isn't available.</summary>
    DevTool? Find(string toolId);
}
