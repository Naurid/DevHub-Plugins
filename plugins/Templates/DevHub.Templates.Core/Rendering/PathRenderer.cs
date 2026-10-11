namespace DevHub.Templates.Core.Rendering;

/// <summary>A rendered path, or why it can't be used.</summary>
/// <param name="Path">Relative, "/"-separated; null when <paramref name="Error"/> is set.</param>
public sealed record RenderedPath(string? Path, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Renders file and folder names (§5 rule 5): every segment of a relative path separately, then
/// checks each one is a usable name: not empty, no separator (a value can't sneak in a
/// "../" or a folder), not "." or "..", no characters Windows forbids, not a reserved device name
/// (CON, NUL, COM1…), no trailing dot or space (Windows drops those). The error names the
/// variable(s) that made the segment.
/// </summary>
public static class PathRenderer
{
    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { "COM" + i, "LPT" + i })],
        StringComparer.OrdinalIgnoreCase);

    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '|', '?', '*', .. Enumerable.Range(0, 32).Select(c => (char)c)];
    private static readonly char[] InvalidGlobNameChars = ['<', '>', ':', '"', '|', .. Enumerable.Range(0, 32).Select(c => (char)c)];

    /// <summary>One file or folder name (a <c>rootFolder</c>, a step's rename target).</summary>
    public static RenderedPath RenderSegment(string segment, RenderContext context) =>
        RenderPath(segment, context, allowGlobs: false, singleSegment: true);

    /// <summary>
    /// A relative path ("src/{{Name}}/{{Name}}.csproj"): each segment rendered and checked.
    /// <paramref name="allowGlobs"/>: step paths may contain <c>*</c>, <c>**</c> and <c>?</c>.
    /// </summary>
    public static RenderedPath RenderPath(string path, RenderContext context, bool allowGlobs = false) =>
        RenderPath(path, context, allowGlobs, singleSegment: false);

    private static RenderedPath RenderPath(string path, RenderContext context, bool allowGlobs, bool singleSegment)
    {
        var segments = singleSegment ? [path] : path.Split('/');
        var rendered = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            // A trailing "/" (a folder target in a move step) leaves an empty last segment: keep it.
            if (segment.Length == 0 && !singleSegment && rendered.Count > 0 && rendered.Count == segments.Length - 1)
            {
                rendered.Add("");
                continue;
            }

            var result = PlaceholderRenderer.RenderWithInfo(segment, context, pathSyntax: true);
            if (Problem(result.Text, allowGlobs) is { } problem)
            {
                var because = result.UsedVariables.Count switch
                {
                    0 => "",
                    1 => $" (from {{{{{result.UsedVariables[0]}}}}})",
                    _ => $" (from {string.Join(", ", result.UsedVariables.Select(v => "{{" + v + "}}"))})",
                };
                return new RenderedPath(null, $"\"{result.Text}\" in \"{path}\" {problem}{because}.");
            }
            rendered.Add(result.Text);
        }
        return new RenderedPath(string.Join('/', rendered), null);
    }

    private static string? Problem(string name, bool allowGlobs)
    {
        if (name.Length == 0 || string.IsNullOrWhiteSpace(name))
            return "is empty";
        if (name.Contains('/') || name.Contains('\\'))
            return "contains a slash, so it isn't one name";
        if (name is "." or "..")
            return "isn't allowed as a name";
        if (name.IndexOfAny(allowGlobs ? InvalidGlobNameChars : InvalidNameChars) >= 0)
            return "contains a character Windows doesn't allow in names";
        if (name.EndsWith('.') || name.EndsWith(' '))
            return "ends with a dot or space, which Windows drops";
        var stem = name.Split('.')[0];
        if (ReservedNames.Contains(stem))
            return "is a name Windows reserves";
        return null;
    }
}
