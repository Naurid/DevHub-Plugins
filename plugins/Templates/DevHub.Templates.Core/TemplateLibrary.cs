using DevHub.Sdk.Core.Settings;

namespace DevHub.Templates.Core;

/// <summary>Where a library comes from (§2).</summary>
public enum LibrarySource
{
    /// <summary>Your own library, <c>%APPDATA%/DevHub/templates</c> unless moved in settings.</summary>
    Personal,

    /// <summary>A folder you added (a team repo, a project's templates folder).</summary>
    Extra,

    /// <summary>Shipped with DevHub, read-only (Duplicate to change one).</summary>
    BuiltIn,
}

/// <summary>A folder of templates: one sub-folder per template, named after its id.</summary>
/// <param name="Name">Shown in the browser ("Personal", "Built-in", or the folder's name).</param>
/// <param name="Root">The library folder.</param>
public sealed record TemplateLibrary(string Name, string Root, LibrarySource Source)
{
    public bool IsReadOnly => Source == LibrarySource.BuiltIn;

    public override string ToString() => Name;
}

/// <summary>The Templates plugin's settings section: which libraries to use.</summary>
public sealed class TemplatesSettings : ISettingsSection
{
    public static string SectionId => "devhub.templates";

    /// <summary>The personal library's folder; null = <c>%APPDATA%/DevHub/templates</c>.</summary>
    public string? PersonalLibrary { get; set; }

    /// <summary>More library folders, in the order they're shown.</summary>
    public List<string> ExtraLibraries { get; set; } = new();
}

/// <summary>Works out the libraries in use from the settings.</summary>
public static class TemplateLibraries
{
    /// <summary>The personal library's default folder under a base folder (the roaming AppData folder in the app).</summary>
    public static string DefaultPersonalRoot(string appDataRoot) => Path.Combine(appDataRoot, "DevHub", "templates");

    /// <summary>Personal first, then the extra folders (named after the folder; duplicates and the personal one skipped), then the built-ins.</summary>
    /// <param name="appDataRoot">Base for the default personal library (the app passes %APPDATA% or DEVHUB_DATA_DIR).</param>
    /// <param name="builtInRoot">The built-in templates shipped with the plugin.</param>
    public static IReadOnlyList<TemplateLibrary> Resolve(TemplatesSettings settings, string appDataRoot, string builtInRoot)
    {
        var personal = Normalize(settings.PersonalLibrary ?? DefaultPersonalRoot(appDataRoot));
        var libraries = new List<TemplateLibrary> { new("Personal", personal, LibrarySource.Personal) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { personal };

        foreach (var folder in settings.ExtraLibraries.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            var root = Normalize(folder);
            if (!seen.Add(root))
                continue;
            var name = Path.GetFileName(root.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : root;
            libraries.Add(new TemplateLibrary(name, root, LibrarySource.Extra));
        }

        libraries.Add(new TemplateLibrary("Built-in", Normalize(builtInRoot), LibrarySource.BuiltIn));
        return libraries;
    }

    /// <summary>Full path without a trailing separator, so a folder with and without a final slash is one library.</summary>
    private static string Normalize(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
}
