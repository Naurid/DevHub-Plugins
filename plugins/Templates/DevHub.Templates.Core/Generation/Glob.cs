using System.Text;
using System.Text.RegularExpressions;

namespace DevHub.Templates.Core.Generation;

/// <summary>
/// Wildcards in step paths (§6), matched against "/"-separated paths relative to the root:
/// <c>*</c> = any characters within one name, <c>?</c> = one character, <c>**</c> = any number of
/// folders (zero included): <c>*.cs</c> matches root-level files, <c>**/*.cs</c> every .cs file,
/// <c>src/**</c> everything under src. Case-insensitive (Windows paths).
/// </summary>
public sealed class Glob
{
    private readonly Regex _regex;

    public Glob(string pattern)
    {
        Pattern = pattern;
        _regex = new Regex("^" + ToRegex(pattern.TrimEnd('/')) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public string Pattern { get; }

    /// <summary>The pattern has wildcards (else it's a plain path, which must exist).</summary>
    public static bool HasWildcards(string pattern) => pattern.IndexOfAny(['*', '?']) >= 0;

    public bool IsMatch(string relativePath) => _regex.IsMatch(relativePath.TrimEnd('/'));

    private static string ToRegex(string pattern)
    {
        var regex = new StringBuilder();
        var segments = pattern.Split('/');
        for (var s = 0; s < segments.Length; s++)
        {
            var segment = segments[s];
            var last = s == segments.Length - 1;
            if (segment == "**")
            {
                // "a/**/b" → a/(any folders/)b; "a/**" → a/anything; "**/b" → (folders/)b.
                regex.Append(last ? ".*" : "(?:[^/]+/)*");
                continue;
            }
            foreach (var c in segment)
            {
                regex.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString()),
                });
            }
            if (!last)
                regex.Append('/');
        }
        return regex.ToString();
    }
}

/// <summary>Resolves relative paths under a root and refuses anything that would end up outside it.</summary>
public static class SafePath
{
    /// <summary>
    /// <paramref name="root"/> + <paramref name="relative"/> ("/"-separated) as a full path, or null
    /// when it would leave the root ("..", an absolute path, a drive letter).
    /// </summary>
    public static string? Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
            return null;
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }

    /// <summary>A full path under <paramref name="root"/> as a "/"-separated relative path.</summary>
    public static string Relative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
}
