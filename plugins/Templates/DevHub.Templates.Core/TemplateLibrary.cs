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

    /// <summary>The <c>{{Author}}</c> built-in; null = the Windows user name.</summary>
    public string? Author { get; set; }

    /// <summary>The folder the last project was created in (the next New Project starts there).</summary>
    public string? LastLocation { get; set; }

    /// <summary>The browser groups by language (true) or by category (false, the default).</summary>
    public bool GroupByLanguage { get; set; }

    /// <summary>Where new projects go unless a rule below says otherwise; null = the last folder used.</summary>
    public string? DefaultProjectFolder { get; set; }

    /// <summary>Where new projects of a language and/or category go (Settings → Templates).</summary>
    public List<ProjectFolderRule> ProjectFolderRules { get; set; } = new();

    /// <summary>The last projects made (newest first, a few only), for New Project's start page.</summary>
    public List<RecentProject> RecentProjects { get; set; } = new();
}

/// <summary>"C# projects go to C:\Dev\dotnet", "Unity projects to D:\Unity": a language, a category, or both.</summary>
public sealed class ProjectFolderRule
{
    public string? Language { get; set; }
    public string? Category { get; set; }
    public string Folder { get; set; } = "";
}

/// <summary>A project New Project made.</summary>
public sealed record RecentProject(string Path, string Template, DateTimeOffset Created);

/// <summary>Where New Project starts: the folder rules, then the default folder, then the last one used.</summary>
public static class ProjectFolders
{
    public const int MaxRecent = 10;

    /// <summary>
    /// The folder for a new project from <paramref name="manifest"/>: the most specific matching rule
    /// (language and category > category > language; the first one listed on a tie), else the default
    /// project folder, else <see cref="TemplatesSettings.LastLocation"/>; null when none is set.
    /// Language and category compare ignoring case; "Any" or an empty field matches everything.
    /// </summary>
    public static string? For(TemplatesSettings settings, TemplateManifest manifest)
    {
        static bool Set(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim() != "Any";

        var best = settings.ProjectFolderRules
            .Where(r => !string.IsNullOrWhiteSpace(r.Folder) && (Set(r.Language) || Set(r.Category)))
            .Where(r => !Set(r.Language) || string.Equals(r.Language!.Trim(), manifest.Language, StringComparison.OrdinalIgnoreCase))
            .Where(r => !Set(r.Category) || manifest.Categories.Contains(r.Category!.Trim(), StringComparer.OrdinalIgnoreCase))
            .Select((r, index) => (Rule: r, Score: (Set(r.Category) ? 2 : 0) + (Set(r.Language) ? 1 : 0), Index: index))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Index)
            .FirstOrDefault().Rule;

        return best?.Folder.Trim()
               ?? (string.IsNullOrWhiteSpace(settings.DefaultProjectFolder) ? null : settings.DefaultProjectFolder.Trim())
               ?? settings.LastLocation;
    }

    /// <summary>Puts a new project first in the recent list (the same folder only once, at most <see cref="MaxRecent"/>).</summary>
    public static void Remember(TemplatesSettings settings, RecentProject project)
    {
        settings.RecentProjects.RemoveAll(r => string.Equals(r.Path, project.Path, StringComparison.OrdinalIgnoreCase));
        settings.RecentProjects.Insert(0, project);
        if (settings.RecentProjects.Count > MaxRecent)
            settings.RecentProjects.RemoveRange(MaxRecent, settings.RecentProjects.Count - MaxRecent);
    }
}

/// <summary>Works out the libraries in use from the settings.</summary>
public static class TemplateLibraries
{
    /// <summary>The personal library's default folder under a base folder (the roaming AppData folder in the app).</summary>
    public static string DefaultPersonalRoot(string appDataRoot) => Path.Combine(appDataRoot, "DevHub", "templates");

    /// <summary>The built-in templates, shipped next to the app (copied from this project's BuiltInTemplates folder).</summary>
    public static string BuiltInRoot => Path.Combine(AppContext.BaseDirectory, "BuiltInTemplates");

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
