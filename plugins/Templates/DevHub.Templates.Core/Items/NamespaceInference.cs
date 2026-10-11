using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace DevHub.Templates.Core.Items;

/// <summary>A namespace worked out for a folder, and where it came from ("from Game.csproj").</summary>
public sealed record NamespaceGuess(string Namespace, string Source);

/// <summary>
/// The C# namespace for files in a folder, the way the IDEs pick it (§7): walk up from the folder to
/// the nearest project and add the folders in between.
/// <list type="bullet">
/// <item>A <c>.csproj</c>: its <c>RootNamespace</c>, else the project's name. <c>src/Player</c> under
///       Game.csproj → <c>Game.Player</c>.</item>
/// <item>A Unity <c>.asmdef</c> with a <c>rootNamespace</c>: that, plus the folders below it.</item>
/// <item>A Unity project (a folder with <c>Assets</c> and <c>ProjectSettings</c>): the project's name,
///       plus the folders below <c>Assets</c> (a leading <c>Scripts</c> folder is skipped).</item>
/// </list>
/// The walk stops at a repository root (<c>.git</c>) or the drive. Folder names become valid
/// identifiers (<c>my folder</c> → <c>my_folder</c>, <c>3D</c> → <c>_3D</c>).
/// </summary>
public static class NamespaceInference
{
    /// <summary>The variable name that gets the guess as its default in an item template's form.</summary>
    public const string VariableName = "Namespace";

    public static NamespaceGuess? Infer(string folder)
    {
        var start = new DirectoryInfo(Path.GetFullPath(folder));
        var below = new List<string>(); // folder names between the project and the target, nearest last
        for (var dir = start; dir is not null; dir = dir.Parent)
        {
            if (!dir.Exists)
            {
                below.Insert(0, dir.Name);
                continue;
            }

            if (FromCsproj(dir) is { } csproj)
                return Guess(csproj.Root, below, $"from {csproj.File}");
            if (FromAsmdef(dir) is { } asmdef)
                return Guess(asmdef.Root, below, $"from {asmdef.File}");
            if (IsUnityProject(dir))
            {
                // Inside Assets: the folders below it, without Assets itself and a leading Scripts.
                var inside = below.Count > 0 && below[0].Equals("Assets", StringComparison.OrdinalIgnoreCase) ? below.Skip(1).ToList() : below;
                if (inside.Count > 0 && inside[0].Equals("Scripts", StringComparison.OrdinalIgnoreCase))
                    inside = inside.Skip(1).ToList();
                return Guess(Identifier(dir.Name), inside, "from the Unity project's name");
            }
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return null; // the repository's root: no project above it counts
            below.Insert(0, dir.Name);
        }
        return null;
    }

    private static NamespaceGuess? Guess(string root, IEnumerable<string> folders, string source)
    {
        var parts = new[] { root }.Concat(folders.Select(Identifier)).Where(p => p.Length > 0).ToList();
        return parts.Count == 0 ? null : new NamespaceGuess(string.Join('.', parts), source);
    }

    private static (string Root, string File)? FromCsproj(DirectoryInfo dir)
    {
        var project = dir.EnumerateFiles("*.csproj").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (project is null)
            return null;
        var name = Path.GetFileNameWithoutExtension(project.Name);
        string? root = null;
        try
        {
            root = XDocument.Load(project.FullName).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "RootNamespace")?.Value.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Unreadable project file: its name will do.
        }
        root = string.IsNullOrEmpty(root) ? name : root.Replace("$(MSBuildProjectName)", name, StringComparison.OrdinalIgnoreCase);
        return (string.Join('.', root.Split('.').Select(Identifier)), project.Name);
    }

    private static (string Root, string File)? FromAsmdef(DirectoryInfo dir)
    {
        var asmdef = dir.EnumerateFiles("*.asmdef").FirstOrDefault();
        if (asmdef is null)
            return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(asmdef.FullName),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (json.RootElement.TryGetProperty("rootNamespace", out var value) && value.GetString() is { Length: > 0 } root)
                return (string.Join('.', root.Split('.').Select(Identifier)), asmdef.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable: as if it had no root namespace.
        }
        return null; // an assembly without a root namespace: keep looking (the Unity project's name)
    }

    private static bool IsUnityProject(DirectoryInfo dir) =>
        Directory.Exists(Path.Combine(dir.FullName, "Assets")) && Directory.Exists(Path.Combine(dir.FullName, "ProjectSettings"));

    /// <summary>A folder name as a C# identifier: letters, digits and _, not starting with a digit.</summary>
    public static string Identifier(string name)
    {
        var builder = new StringBuilder(name.Length + 1);
        foreach (var c in name)
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (builder.Length > 0 && char.IsDigit(builder[0]))
            builder.Insert(0, '_');
        return builder.ToString();
    }
}
