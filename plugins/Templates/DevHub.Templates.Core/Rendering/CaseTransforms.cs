using System.Text;

namespace DevHub.Templates.Core.Rendering;

/// <summary>
/// The placeholder transforms (docs/TEMPLATE_FORMAT.md §5): <c>{{Name:pascal}}</c> etc.
/// pascal / camel / kebab / snake work on words; upper / lower change the whole value as is.
/// </summary>
public static class CaseTransforms
{
    /// <summary>The transform names a placeholder may use.</summary>
    public static readonly IReadOnlySet<string> Names =
        new HashSet<string>(["pascal", "camel", "kebab", "snake", "upper", "lower"], StringComparer.Ordinal);

    /// <summary>Applies a known transform; null for an unknown name (the token is then left as written).</summary>
    public static string? Apply(string transform, string value) => transform switch
    {
        "pascal" => string.Concat(Words(value).Select(Capitalize)),
        "camel" => Camel(Words(value)),
        "kebab" => string.Join('-', Words(value).Select(w => w.ToLowerInvariant())),
        "snake" => string.Join('_', Words(value).Select(w => w.ToLowerInvariant())),
        "upper" => value.ToUpperInvariant(),
        "lower" => value.ToLowerInvariant(),
        _ => null,
    };

    /// <summary>
    /// Splits a value into words: on spaces, <c>_</c>, <c>-</c>, <c>.</c> and other non-alphanumerics,
    /// and on case changes: <c>myHTTPServer</c> → my, HTTP, Server; <c>LumberjackHollow2</c> →
    /// Lumberjack, Hollow2 (digits stay with the word before them).
    /// </summary>
    public static IReadOnlyList<string> Words(string value)
    {
        var words = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
                words.Add(current.ToString());
            current.Clear();
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (current.Length > 0 && char.IsUpper(c))
            {
                var previous = value[i - 1];
                var nextIsLower = i + 1 < value.Length && char.IsLower(value[i + 1]);
                // "myHttp": lower/digit → Upper starts a word. "HTTPServer": the S starts one
                // (an upper followed by a lower, after an upper run).
                if (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && nextIsLower))
                    Flush();
            }
            current.Append(c);
        }
        Flush();
        return words;
    }

    private static string Camel(IReadOnlyList<string> words) =>
        words.Count == 0 ? "" : words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(Capitalize));

    private static string Capitalize(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
}
