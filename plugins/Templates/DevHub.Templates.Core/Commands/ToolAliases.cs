using System.Text.Json;

namespace DevHub.Templates.Core.Commands;

/// <summary>A Unity editor installed through Unity Hub.</summary>
public sealed record UnityEditor(string Version, string Path);

/// <summary>
/// DevHub's tool aliases for template commands (<c>@unity</c>): tools that aren't on PATH but have a
/// known install place. <c>@unity</c> is the newest Unity editor Unity Hub installed (in its default
/// folder, or the one set in Hub's preferences).
/// </summary>
public static class ToolAliases
{
    /// <summary>"unity" → the newest editor's Unity.exe; null for an unknown alias or when none is installed.</summary>
    public static string? Resolve(string alias) =>
        alias.Equals("unity", StringComparison.OrdinalIgnoreCase) ? UnityEditors().FirstOrDefault()?.Path : null;

    /// <summary>The installed editors, newest first.</summary>
    /// <param name="hubFolders">Folders holding one folder per version (tests pass their own); null = Unity Hub's.</param>
    public static IReadOnlyList<UnityEditor> UnityEditors(IEnumerable<string>? hubFolders = null)
    {
        var editors = new List<UnityEditor>();
        foreach (var folder in (hubFolders ?? HubFolders()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder))
                continue;
            foreach (var version in Directory.EnumerateDirectories(folder))
            {
                var exe = Path.Combine(version, "Editor", "Unity.exe");
                if (File.Exists(exe))
                    editors.Add(new UnityEditor(Path.GetFileName(version), exe));
            }
        }
        return editors.OrderByDescending(e => e.Version, UnityVersionComparer.Instance).ToList();
    }

    private static IEnumerable<string> HubFolders()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor");
        // Hub's "Installs location" preference: a JSON string in this file.
        var secondary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub", "secondaryInstallPath.json");
        string? path = null;
        try
        {
            if (File.Exists(secondary))
                path = JsonSerializer.Deserialize<string>(File.ReadAllText(secondary));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable preference: the default folder only.
        }
        if (!string.IsNullOrWhiteSpace(path))
            yield return path;
    }

    /// <summary>Orders "6000.3.13f1" after "6000.2.7f2" and "2022.3.10f1": numbers compared as numbers, then the release letter (a &lt; b &lt; f &lt; p).</summary>
    public sealed class UnityVersionComparer : IComparer<string>
    {
        public static readonly UnityVersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Parts(x ?? "");
            var b = Parts(y ?? "");
            for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
            {
                var pa = i < a.Count ? a[i] : (0L, "");
                var pb = i < b.Count ? b[i] : (0L, "");
                var c = pa.Item1.CompareTo(pb.Item1);
                if (c == 0)
                    c = string.CompareOrdinal(pa.Item2, pb.Item2);
                if (c != 0)
                    return c;
            }
            return 0;
        }

        /// <summary>"6000.3.13f1" → (6000,""),(3,""),(13,"f"),(1,"")</summary>
        private static List<(long, string)> Parts(string version)
        {
            var parts = new List<(long, string)>();
            var number = 0L;
            var letters = "";
            var inNumber = false;
            foreach (var c in version)
            {
                if (char.IsAsciiDigit(c))
                {
                    if (!inNumber && letters.Length > 0)
                    {
                        parts[^1] = (parts[^1].Item1, letters);
                        letters = "";
                    }
                    number = number * 10 + (c - '0');
                    inNumber = true;
                }
                else
                {
                    if (inNumber)
                        parts.Add((number, ""));
                    number = 0;
                    inNumber = false;
                    if (char.IsAsciiLetter(c))
                        letters += char.ToLowerInvariant(c);
                }
            }
            if (inNumber)
                parts.Add((number, ""));
            return parts;
        }
    }
}
