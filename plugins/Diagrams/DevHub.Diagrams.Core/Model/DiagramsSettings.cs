using DevHub.Sdk.Core.Settings;

namespace DevHub.Diagrams.Core;

/// <summary>Where exported images (PNG, SVG) go.</summary>
public enum ExportPlace
{
    /// <summary>A save dialog every time (the default).</summary>
    Ask,

    /// <summary>Next to the diagram file (in <see cref="DiagramsSettings.BesideSubfolder"/> if set).</summary>
    BesideDiagram,

    /// <summary>One folder for every diagram (<see cref="DiagramsSettings.ExportFolder"/>).</summary>
    Folder,
}

/// <summary>Settings → Diagrams.</summary>
public sealed class DiagramsSettings : ISettingsSection
{
    public static string SectionId => "devhub.diagrams";

    public ExportPlace ExportPlace { get; set; } = ExportPlace.Ask;

    /// <summary>With <see cref="ExportPlace.Folder"/>: the folder.</summary>
    public string? ExportFolder { get; set; }

    /// <summary>With <see cref="ExportPlace.BesideDiagram"/>: a subfolder ("images"), or null for the diagram's own folder.</summary>
    public string? BesideSubfolder { get; set; }

    /// <summary>
    /// The file an image export writes, or null to ask (a save dialog). The name is the diagram's title; exporting
    /// again replaces the image, so documents linking to it stay up to date.
    /// </summary>
    public string? TargetFor(string? diagramPath, string title, string extension)
    {
        var name = string.Concat((title.Trim().Length > 0 ? title.Trim() : "diagram").Split(Path.GetInvalidFileNameChars())) + "." + extension;
        switch (ExportPlace)
        {
            case ExportPlace.Folder when ExportFolder is { Length: > 0 } folder:
                return Path.Combine(folder, name);
            case ExportPlace.BesideDiagram when diagramPath is not null && Path.GetDirectoryName(diagramPath) is { } dir:
                return BesideSubfolder is { Length: > 0 } sub ? Path.Combine(dir, sub, name) : Path.Combine(dir, name);
            default:
                return null;
        }
    }
}
