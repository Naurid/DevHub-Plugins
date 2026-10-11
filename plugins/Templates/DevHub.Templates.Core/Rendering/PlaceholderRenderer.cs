using System.Text;

namespace DevHub.Templates.Core.Rendering;

/// <summary>Rendered text, with the variables it used (to name them when a rendered path is invalid).</summary>
public sealed record RenderedText(string Text, IReadOnlyList<string> UsedVariables);

/// <summary>
/// Substitutes <c>{{Name}}</c> and <c>{{Name:transform}}</c> (§5) in one pass over the text:
/// <list type="bullet">
/// <item>Only an exact declared or built-in name with a known transform (or none) is replaced;
///       anything else (<c>{{ item }}</c>, <c>${{ secrets.X }}</c>, <c>{{#each}}</c>,
///       <c>{{Unknown}}</c>, <c>{{{body}}}</c>) is copied exactly as written.</item>
/// <item>A substituted value is never scanned again, so a value containing <c>{{Other}}</c> stays literal.</item>
/// <item><c>\{{</c> is a literal <c>{{</c> (the backslash is dropped), even before a valid name.</item>
/// </list>
/// </summary>
public static class PlaceholderRenderer
{
    public static string Render(string text, RenderContext context) => RenderWithInfo(text, context).Text;

    public static RenderedText RenderWithInfo(string text, RenderContext context)
    {
        // Fast path: most file contents have no braces at all.
        if (!text.Contains("{{", StringComparison.Ordinal))
            return new RenderedText(text, []);

        var output = new StringBuilder(text.Length);
        var used = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\\' && At(text, i + 1, "{{"))
            {
                output.Append("{{");
                i += 3;
                continue;
            }

            if (!At(text, i, "{{"))
            {
                output.Append(text[i]);
                i++;
                continue;
            }

            var close = text.IndexOf("}}", i + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                output.Append(text, i, text.Length - i); // unclosed: the rest as written
                break;
            }

            var inner = text[(i + 2)..close];
            if (TryRender(inner, context, out var value, out var name))
            {
                output.Append(value);
                if (!used.Contains(name))
                    used.Add(name);
                i = close + 2;
            }
            else
            {
                // Not ours (another template language): copy the two braces and go on after them,
                // so "{{{x}}}" or "{{ a }}" come out exactly as they were.
                output.Append("{{");
                i += 2;
            }
        }
        return new RenderedText(output.ToString(), used);
    }

    /// <summary>"Name" or "Name:transform", exactly: letters/digits, no spaces, a known name and transform.</summary>
    private static bool TryRender(string inner, RenderContext context, out string value, out string name)
    {
        value = "";
        var colon = inner.IndexOf(':');
        name = colon < 0 ? inner : inner[..colon];
        var transform = colon < 0 ? null : inner[(colon + 1)..];

        if (!IsName(name) || context[name] is not { } raw)
            return false;
        if (transform is null)
        {
            value = raw;
            return true;
        }
        if (CaseTransforms.Apply(transform, raw) is not { } transformed)
            return false;
        value = transformed;
        return true;
    }

    private static bool IsName(string name) =>
        name.Length > 0 && char.IsAsciiLetter(name[0]) && name.All(char.IsAsciiLetterOrDigit);

    private static bool At(string text, int index, string value) =>
        index + value.Length <= text.Length && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;
}
