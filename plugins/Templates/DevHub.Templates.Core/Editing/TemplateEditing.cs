using DevHub.Templates.Core.Generation;
using DevHub.Templates.Core.Rendering;

namespace DevHub.Templates.Core.Editing;

/// <summary>Template ids (§2: lowercase kebab-case, the folder name).</summary>
public static class TemplateIds
{
    /// <summary>
    /// "Unity 3D Game!" → "unity-3d-game", "MyGame" → "my-game"; "template" when nothing usable is
    /// left. Words break at anything but ASCII letters and digits, and where a lowercase letter meets
    /// an uppercase one (not at digits: "3D" stays "3d").
    /// </summary>
    public static string FromName(string name)
    {
        var chars = new List<char>();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (char.IsAsciiLetterUpper(c) && i > 0 && char.IsAsciiLetterLower(name[i - 1]))
                    chars.Add('-');
                chars.Add(char.ToLowerInvariant(c));
            }
            else
            {
                chars.Add('-');
            }
        }
        var id = string.Join('-', new string(chars.ToArray()).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return id.Length > 0 ? id : "template";
    }

    /// <summary>The id, or "id-2", "id-3"… when a template with it already exists in the library.</summary>
    public static string Unique(TemplateLibrary library, string id)
    {
        var candidate = id;
        for (var n = 2; Directory.Exists(Path.Combine(library.Root, candidate)); n++)
            candidate = $"{id}-{n}";
        return candidate;
    }
}

/// <summary>How the editor's text boxes hold lists: "Console, .NET" (categories), one argument per line (commands).</summary>
public static class ListText
{
    /// <summary>Comma- or line-separated, trimmed, without empties and duplicates (ignoring case).</summary>
    public static IReadOnlyList<string> SplitList(string text) =>
        text.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string JoinList(IEnumerable<string> items) => string.Join(", ", items);

    /// <summary>
    /// One item per line, kept as typed except the line break (an argument may have spaces:
    /// "My App" stays one argument). Empty lines are dropped.
    /// </summary>
    public static IReadOnlyList<string> SplitLines(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Where(l => l.Length > 0).ToList();

    public static string JoinLines(IEnumerable<string> items) => string.Join(Environment.NewLine, items);
}

/// <summary>The template a "New Template" starts as.</summary>
public static class NewTemplate
{
    /// <summary>Languages offered in the editor (the user may type any other).</summary>
    public static readonly IReadOnlyList<string> KnownLanguages =
        ["Any", "C", "C#", "C++", "Dart", "GDScript", "Go", "Java", "JavaScript", "Kotlin", "Lua", "PHP", "Python", "Ruby", "Rust", "Shell", "Swift", "TypeScript"];

    /// <summary>A valid manifest with a required Name variable (a project's folder is named after it).</summary>
    public static TemplateManifest Create(string name, string id, TemplateKind kind, string language) => new()
    {
        SchemaVersion = TemplateManifest.CurrentSchemaVersion,
        Id = id,
        Name = name.Trim(),
        Kind = kind,
        Language = string.IsNullOrWhiteSpace(language) ? "Any" : language.Trim(),
        Variables = [new TemplateVariable { Name = "Name", Label = kind == TemplateKind.Project ? "Project name" : "Name", Required = true }],
    };
}

/// <summary>A hand-edited template.json (the editor's JSON view): the manifest, or why it can't be used.</summary>
public static class ManifestText
{
    /// <returns>The manifest (also when it has validation issues, so the form can show it), and every problem.</returns>
    public static (TemplateManifest? Manifest, IReadOnlyList<string> Problems) Parse(string json)
    {
        var read = ManifestSerializer.Read(json);
        if (read.Manifest is not { } manifest)
            return (null, [read.Error!]);
        return (manifest, ManifestValidator.Validate(manifest).Select(i => i.ToString()).ToList());
    }
}

/// <summary>A file or folder in a template's <c>content/</c> folder ("/"-separated, relative to it).</summary>
public sealed record ContentItem(string Path, bool IsFolder)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
    public string Parent => Path.LastIndexOf('/') is var slash and >= 0 ? Path[..slash] : "";
}

/// <summary>
/// Changes to a template's <c>content/</c> folder from the editor's Files section. They happen on
/// disk right away (not on Save), never outside content/, never in a read-only library. Names
/// may contain placeholders (<c>{{Name}}.cs</c>) but must be valid Windows names: transforms in a
/// file name use a dot (<c>{{Name.kebab}}</c>), because Windows doesn't allow ':' there.
/// </summary>
public static class TemplateContent
{
    /// <summary>Everything in content/, folders before what's in them (sorted by path).</summary>
    public static IReadOnlyList<ContentItem> List(TemplateEntry template)
    {
        if (!Directory.Exists(template.ContentFolder))
            return [];
        return new DirectoryInfo(template.ContentFolder)
            .EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .Where(i => i.LinkTarget is null)
            .Select(i => new ContentItem(SafePath.Relative(template.ContentFolder, i.FullName), i is DirectoryInfo))
            .OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The full path of an item, or null when the relative path leaves content/.</summary>
    public static string? FullPath(TemplateEntry template, string path) => SafePath.Resolve(template.ContentFolder, path);

    /// <summary>Creates an empty file in <paramref name="parent"/> ("" = content/ itself). Returns the error or null.</summary>
    public static string? CreateFile(TemplateEntry template, string parent, string name) =>
        Create(template, parent, name, full => File.WriteAllBytes(full, []));

    public static string? CreateFolder(TemplateEntry template, string parent, string name) =>
        Create(template, parent, name, full => Directory.CreateDirectory(full));

    /// <summary>Renames a file or folder in place (a case-only rename works).</summary>
    public static string? Rename(TemplateEntry template, string path, string newName)
    {
        newName = newName.Trim();
        if (Check(template, newName) is { } problem)
            return problem;
        var from = FullPath(template, path);
        if (from is null || path.Length == 0 || !(File.Exists(from) || Directory.Exists(from)))
            return $"\"{path}\" doesn't exist.";
        var to = Path.Combine(Path.GetDirectoryName(from)!, newName);
        var caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(to) || Directory.Exists(to)))
            return $"\"{newName}\" already exists there.";

        if (Directory.Exists(from))
        {
            if (caseOnly) // Windows can't rename a folder to itself in another case directly: go through a temporary name.
            {
                var temp = from + ".renaming";
                Directory.Move(from, temp);
                Directory.Move(temp, to);
            }
            else
            {
                Directory.Move(from, to);
            }
        }
        else
        {
            File.Move(from, to);
        }
        return null;
    }

    /// <summary>Deletes a file, or a folder with everything in it.</summary>
    public static string? Delete(TemplateEntry template, string path)
    {
        if (template.Library.IsReadOnly)
            return ReadOnly(template);
        var full = FullPath(template, path);
        if (full is null || path.Length == 0)
            return $"\"{path}\" isn't inside the template's files.";
        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
        else if (File.Exists(full))
            File.Delete(full);
        else
            return $"\"{path}\" doesn't exist.";
        return null;
    }

    private static string? Create(TemplateEntry template, string parent, string name, Action<string> create)
    {
        name = name.Trim();
        if (Check(template, name) is { } problem)
            return problem;
        var folder = FullPath(template, parent.Length == 0 ? "." : parent);
        if (folder is null)
            return $"\"{parent}\" isn't inside the template's files.";
        var full = Path.Combine(folder, name);
        if (File.Exists(full) || Directory.Exists(full))
            return $"\"{name}\" already exists there.";
        Directory.CreateDirectory(folder);
        create(full);
        return null;
    }

    private static string? Check(TemplateEntry template, string name)
    {
        if (template.Library.IsReadOnly)
            return ReadOnly(template);
        name = name.Trim();
        if (name.Length == 0)
            return "Enter a name.";
        if (name.Contains("{{", StringComparison.Ordinal) && name.Contains(':'))
            return $"\"{name}\": Windows doesn't allow ':' in names; write a transform with a dot, like {{{{Name.kebab}}}}.";
        return PathRenderer.NameProblem(name);
    }

    private static string ReadOnly(TemplateEntry template) =>
        $"\"{template.Library.Name}\" is read-only: duplicate the template into your own library to change it.";
}
