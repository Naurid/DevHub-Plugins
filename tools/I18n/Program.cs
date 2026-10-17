using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Translation helper for DevHub and its plugins (README.md next to this file):
//   list  <project>              C# strings that look like UI text and aren't translated yet (file:line:col, text, code)
//   wrap  <project> [skip.txt]   wraps those strings in L.T(…), except the file:line:col lines in skip.txt
//   xaml  <project>              wraps literal text attributes in the project's .axaml files in {l:T '…'}
//   keys  <project|root>         collects every key into <project>/i18n/fr.json (new ones empty), reports what's missing
//   check <project|root>         fails when a key has no French text
// A <root> (a folder with several projects) runs the command on every project under it except tests and tools.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: I18n list|wrap|xaml|keys|check <project folder> [skip file]");
    return 2;
}
var command = args[0];
var folder = Path.GetFullPath(args[1]);
var projects = Projects(folder).ToList();
var failed = false;
foreach (var project in projects)
{
    switch (command)
    {
        case "list-loose":
            CSharp.Loose = true;
            goto case "list";
        case "wrap-loose":
            CSharp.Loose = true;
            goto case "wrap";
        case "list":
            foreach (var c in CSharp.Candidates(project))
                Console.WriteLine($"{c.Id}\t{c.Text}\t{c.Context}");
            break;
        case "wrap":
            var skip = args.Length > 2 ? File.ReadAllLines(args[2]).Select(l => l.Split('\t')[0].Trim()).Where(l => l.Length > 0).ToHashSet() : [];
            Console.WriteLine($"{Files.Rel(project)}: {CSharp.Wrap(project, skip)} strings wrapped");
            break;
        case "xaml":
            Console.WriteLine($"{Files.Rel(project)}: {Xaml.Wrap(project)} attributes wrapped");
            break;
        case "keys":
            Keys.Update(project);
            break;
        case "check":
            failed |= !Keys.Check(project);
            break;
        case "export":
            Keys.Export(project);
            break;
        case "import":
            Keys.Import(project, args[2]);
            break;
        case "add":
            Keys.Add(project, args[2]);
            break;
        case "typo":
            Keys.Typography(project);
            break;
        default:
            Console.Error.WriteLine($"unknown command {command}");
            return 2;
    }
}
return failed ? 1 : 0;

static IEnumerable<string> Projects(string folder)
{
    if (Directory.EnumerateFiles(folder, "*.csproj").Any())
        return [folder];
    return Directory.EnumerateFiles(folder, "*.csproj", SearchOption.AllDirectories)
        .Select(Path.GetDirectoryName).OfType<string>()
        .Where(d => !Regex.IsMatch(d, @"[\\/](tests|tools|bin|obj)[\\/]|\.Tests$|TestPlugin|TestKit|HelloWorld"))
        .Order(StringComparer.OrdinalIgnoreCase);
}

static class Files
{
    public static string Rel(string path) => Path.GetRelativePath(Environment.CurrentDirectory, path);

    public static IEnumerable<string> Sources(string project, string extension) =>
    Directory.EnumerateFiles(project, "*" + extension, SearchOption.AllDirectories)
        .Where(f => !Regex.IsMatch(Path.GetRelativePath(project, f), @"^(bin|obj)[\\/]"))
        .Order(StringComparer.Ordinal);
}

/// <summary>A C# string that looks like UI text: where it is, its text, and the line of code around it.</summary>
record Candidate(string Id, string Text, string Context, int Start, int End, bool Interpolated);

static class CSharp
{
    // Calls whose string arguments are code, not text (comparisons, paths, parsing, logs…).
    private static readonly HashSet<string> CodeCalls =
    [
        "StartsWith", "EndsWith", "Contains", "Equals", "Replace", "Split", "IndexOf", "LastIndexOf", "TrimStart", "TrimEnd", "Trim",
        "GetEnvironmentVariable", "Combine", "GetFullPath", "Join", "IsMatch", "Match", "Matches", "Regex", "GetProperty", "TryGetProperty",
        "TryGetValue", "ContainsKey", "GetValueOrDefault", "WriteLine", "Write", "Assert", "Fail", "Run", "Start", "ParseExact", "TryParseExact",
        "Parse", "TryParse", "ToString", "nameof", "GetManifestResourceStream", "Load", "CreateUri", "Uri", "AddArgument", "Arguments",
        "Process", "Command", "Has", "Register", "RegisterAttached", "RegisterDirect", "GetResourceObservable", "FindResource", "TryFindResource", "Token", "RunGit", "Git", "GitAsync", "RunAsync", "ReadAsync", "Set", "Get", "Remove", "Add",
    ];

    // Members whose strings are ids or names the code matches on (menus group by Menu, translated where they're shown).
    private static readonly HashSet<string> CodeMembers = ["Gesture", "Menu", "Category", "Icon", "Id", "Kind", "SectionId", "Extension"];

    private static readonly HashSet<string> DeveloperExceptions =
    [
        "ArgumentException", "ArgumentNullException", "ArgumentOutOfRangeException", "InvalidOperationException",
        "NotSupportedException", "NotImplementedException", "KeyNotFoundException", "UnreachableException", "ObjectDisposedException",
    ];

    public static IEnumerable<Candidate> Candidates(string project)
    {
        foreach (var file in Files.Sources(project, ".cs"))
        {
            var text = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(text, path: file);
            foreach (var c in Candidates(tree, Path.GetRelativePath(project, file)))
                yield return c;
        }
    }

    private static IEnumerable<Candidate> Candidates(SyntaxTree tree, string file)
    {
        var root = tree.GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            string? value;
            bool interpolated;
            switch (node)
            {
                case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
                    when literal.Token.IsKind(SyntaxKind.StringLiteralToken) && !literal.Token.Text.StartsWith('@'):
                    value = literal.Token.ValueText;
                    interpolated = false;
                    break;
                case InterpolatedStringExpressionSyntax s when s.StringStartToken.IsKind(SyntaxKind.InterpolatedStringStartToken):
                    value = string.Concat(s.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText));
                    interpolated = true;
                    break;
                default:
                    continue;
            }
            // A word inside a value of an interpolated text ("is" : "are") is text too.
            var inText = node.Ancestors().OfType<InterpolationSyntax>().Any() && value.Any(char.IsLetter) && !value.Contains('/') && !value.Contains('\\');
            if (!(inText || LooksLikeText(value, interpolated)) || IsCode(node))
                continue;
            var line = tree.GetLineSpan(node.Span).StartLinePosition;
            var context = node.SyntaxTree.GetText().Lines[line.Line].ToString().Trim();
            if (context.Length > 140)
                context = context[..140];
            yield return new Candidate($"{file}:{line.Line + 1}:{line.Character + 1}", node.ToString(), context,
                node.SpanStart, node.Span.End, interpolated);
        }
    }

    // "list-loose" / "wrap-loose": also short and lowercase text ("1 issue", $"{n} days"), and strings given to Add.
    public static bool Loose;

    private static bool LooksLikeText(string value, bool interpolated)
    {
        if (Loose)
            return value.Contains(' ') && Regex.IsMatch(value, @"[A-Za-z]{3,}") && !value.Contains(":://") && !Regex.IsMatch(value.Trim(), @"^[#-]");
        var text = value.Trim();
        if (!text.Any(char.IsLetter) || text.StartsWith('#') || text.StartsWith('-') || text.Contains("://")
            || Regex.IsMatch(text, @"^[\w.\-/\\:]+$") && !Regex.IsMatch(text, @"^[A-Z][a-z]+$") && !text.EndsWith('…'))
            return interpolated && value.Contains(' ') && value.Any(char.IsLetter) && Regex.IsMatch(value, @"[a-z]{2,} [a-z]{2,}");
        return char.IsUpper(text[0]) || text.EndsWith('…') || Regex.IsMatch(text, @"[a-z]{2,} [a-z]{2,}");
    }

    private static bool IsCode(SyntaxNode node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case AttributeArgumentSyntax or CaseSwitchLabelSyntax or ConstantPatternSyntax or InterpolationFormatClauseSyntax:
                    return true;
                case ParameterSyntax:
                    return true;
                case FieldDeclarationSyntax f when f.Modifiers.Any(SyntaxKind.ConstKeyword):
                    return true;
                case LocalDeclarationStatementSyntax l when l.IsConst:
                    return true;
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression):
                    return true;
                case ElementAccessExpressionSyntax or BracketedArgumentListSyntax:
                    return true;
                case AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.Text: var member } } when CodeMembers.Contains(member):
                    return true;
                case InvocationExpressionSyntax invocation when n != node:
                    var name = invocation.Expression switch
                    {
                        MemberAccessExpressionSyntax m => (m.Expression.ToString(), m.Name.Identifier.Text),
                        IdentifierNameSyntax i => ("", i.Identifier.Text),
                        GenericNameSyntax g => ("", g.Identifier.Text),
                        _ => ("", ""),
                    };
                    if (name.Item1 == "L" && name.Item2 is "T" or "F" && IsDirectArgument(node, invocation))
                        return true;   // translated already
                    if (CodeCalls.Contains(name.Item2) && !(Loose && name.Item2 is "Add" or "Insert") && IsDirectArgument(node, invocation))
                        return true;
                    break;
                case ObjectCreationExpressionSyntax creation when IsDirectArgument(node, creation):
                    var type = creation.Type.ToString().Split('.').Last();
                    if (DeveloperExceptions.Contains(type) || type is "Regex" or "Uri" or "ColumnDefinitions" or "RowDefinitions" or "ProcessStartInfo" or "JsonException")
                        return true;
                    break;
                case MemberDeclarationSyntax or StatementSyntax:
                    return false;
            }
        }
        return false;
    }

    // The string is one of the call's arguments itself (not inside a lambda or another expression given to it).
    private static bool IsDirectArgument(SyntaxNode node, SyntaxNode call) =>
        node.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: var owner } } && owner == call;

    public static int Wrap(string project, IReadOnlySet<string> skip)
    {
        var count = 0;
        foreach (var file in Files.Sources(project, ".cs"))
        {
            var text = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(text, path: file);
            var wrap = Candidates(tree, Path.GetRelativePath(project, file)).Where(c => !skip.Contains(c.Id)).ToList();
            if (wrap.Count == 0)
                continue;
            var builder = new StringBuilder(text);
            // From the end, so earlier positions stay right (a string inside an interpolation sits within another).
            foreach (var (position, insert) in wrap.SelectMany(c => new[] { (c.End, ")"), (c.Start, "L.T(") }).OrderByDescending(p => p.Item1))
                builder.Insert(position, insert);
            File.WriteAllText(file, AddUsing(builder.ToString()), new UTF8Encoding(text.Length > 0 && File.ReadAllBytes(file).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })));
            count += wrap.Count;
        }
        return count;
    }

    private const string Using = "using DevHub.Sdk.Core.Localization;";

    private static string AddUsing(string text)
    {
        if (text.Contains(Using))
            return text;
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var usings = Regex.Matches(text, @"^using [^;(=\r\n]+;", RegexOptions.Multiline);
        if (usings.Count > 0)
        {
            var last = usings[^1];
            return text.Insert(last.Index + last.Length, newline + Using.TrimEnd());
        }
        return Using + newline + (text.StartsWith("namespace") ? newline : "") + text;
    }

    /// <summary>The keys of the L.T / L.F calls in a file: the English text, with {0}, {1}… for interpolated values.</summary>
    public static IEnumerable<string> Keys(string text)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "L" }, Name.Identifier.Text: "T" or "F" }
                || invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is not { } argument)
                continue;
            switch (argument)
            {
                case LiteralExpressionSyntax literal when literal.Token.IsKind(SyntaxKind.StringLiteralToken):
                    yield return literal.Token.ValueText;
                    break;
                case InterpolatedStringExpressionSyntax interpolated:
                    yield return Format(interpolated);
                    break;
            }
        }
    }

    // As L.Text builds it: literal text with braces doubled, each value as {index[,alignment][:format]}.
    private static string Format(InterpolatedStringExpressionSyntax s)
    {
        var key = new StringBuilder();
        var index = 0;
        foreach (var part in s.Contents)
        {
            if (part is InterpolatedStringTextSyntax t)
                key.Append(t.TextToken.ValueText.Replace("{{", "{").Replace("}}", "}").Replace("{", "{{").Replace("}", "}}"));
            else if (part is InterpolationSyntax i)
            {
                key.Append('{').Append(index++);
                if (i.AlignmentClause is { } a)
                    key.Append(',').Append(a.Value.ToString().Replace(" ", ""));
                if (i.FormatClause is { } f)
                    key.Append(':').Append(f.FormatStringToken.ValueText);
                key.Append('}');
            }
        }
        return key.ToString();
    }
}

static class Xaml
{
    private static readonly Regex Attribute = new(
        @"(?<=\s)(?<name>Text|Content|Header|ToolTip\.Tip|PlaceholderText|Watermark|Title|Label|Description|Message|Hint|Caption|Explanation|EmptyText|Subtitle|AutomationProperties\.Name|OnContent|OffContent|Placeholder|ActionText|Detail|Summary)=""(?<value>[^""{][^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex Key = new(@"\{l:T (?:'(?<q>(?:\\.|[^'\\])*)'|(?<w>[^}'\s]+))\}", RegexOptions.Compiled);

    public static int Wrap(string project)
    {
        var count = 0;
        foreach (var file in Files.Sources(project, ".axaml"))
        {
            var text = File.ReadAllText(file);
            var changed = 0;
            var result = Attribute.Replace(text, m =>
            {
                var value = m.Groups["value"].Value;
                if (!value.Any(char.IsLetter))
                    return m.Value;
                changed++;
                var escaped = value.Replace(@"\", @"\\").Replace("'", @"\'");
                return $"{m.Groups["name"].Value}=\"{{l:T '{escaped}'}}\"";
            });
            if (changed == 0)
                continue;
            if (!result.Contains("xmlns:l=\"using:DevHub.Localization\""))
            {
                var x = Regex.Match(result, @"(\r?\n)(\s*)xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""");
                result = x.Success
                    ? result.Insert(x.Index + x.Length, $"{x.Groups[1].Value}{x.Groups[2].Value}xmlns:l=\"using:DevHub.Localization\"")
                    : Regex.Replace(result, @"xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""", "$0 xmlns:l=\"using:DevHub.Localization\"");
            }
            File.WriteAllText(file, result);
            count += changed;
        }
        return count;
    }

    public static IEnumerable<string> Keys(string text) =>
        Key.Matches(text).Select(m => m.Groups["q"].Success
            // As the app sees it: XML entities (&quot; &amp;) decoded by the XML reader, then \' unescaped.
            ? Regex.Replace(System.Net.WebUtility.HtmlDecode(m.Groups["q"].Value), @"\\(.)", "$1")
            : m.Groups["w"].Value);
}

static class Keys
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static List<string> Collect(string project) =>
        Files.Sources(project, ".cs").SelectMany(f => CSharp.Keys(File.ReadAllText(f)))
            .Concat(Files.Sources(project, ".axaml").SelectMany(f => Xaml.Keys(File.ReadAllText(f))))
            .Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    private static string FilePath(string project) => Path.Combine(project, "i18n", "fr.json");

    private static Dictionary<string, string> Read(string project) =>
        File.Exists(FilePath(project))
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath(project))) ?? []
            : [];

    // For translating many keys at once: "export" prints the keys still without French as "number<TAB>English"
    // (newlines as \n), "import" reads "number<TAB>French" lines back into the file. Numbers are positions in fr.json.
    public static void Export(string project)
    {
        var entries = Read(project).ToList();
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].Value.Length == 0)
                Console.WriteLine($"{i}\t{entries[i].Key.Replace("\n", "\\n").Replace("\t", "\\t")}");
    }

    public static void Import(string project, string file)
    {
        var entries = Read(project).ToList();
        var changed = 0;
        foreach (var line in File.ReadAllLines(file))
        {
            var tab = line.IndexOf('\t');
            if (tab < 1 || !int.TryParse(line[..tab], out var i) || i < 0 || i >= entries.Count)
                continue;
            entries[i] = new(entries[i].Key, line[(tab + 1)..].Replace("\\n", "\n").Replace("\\t", "\t"));
            changed++;
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in entries)
            map[k] = v;
        File.WriteAllText(FilePath(project), JsonSerializer.Serialize(map, Json).Replace("\r\n", "\n") + "\n");
        Console.WriteLine($"{Files.Rel(project)}: {changed} translations imported");
    }

    // Keys the code doesn't show through L.T literals (plugin names, menus, categories): "English<TAB>French" lines.
    public static void Add(string project, string file)
    {
        var map = Read(project);
        var added = 0;
        foreach (var line in File.ReadAllLines(file))
        {
            var parts = line.Split('\t');
            if (parts.Length != 2 || parts[0].Length == 0)
                continue;
            map[parts[0].Replace("\\n", "\n")] = parts[1].Replace("\\n", "\n");
            added++;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath(project))!);
        File.WriteAllText(FilePath(project), JsonSerializer.Serialize(map, Json).Replace("\r\n", "\n") + "\n");
        Console.WriteLine($"{Files.Rel(project)}: {added} keys added");
    }

    // French typography: a no-break space before : (and a narrow one before ; ? ! %), and inside « ». Without it
    // the sign can wrap onto a line of its own. (The replacements are U+00A0 before :, U+202F before ; ? ! %.)
    public static string French(string text) =>
        text.Replace(" :", " :").Replace(" ;", " ;").Replace(" ?", " ?").Replace(" !", " !")
            .Replace(" %", " %").Replace("« ", "« ").Replace(" »", " »");

    public static void Typography(string project)
    {
        var map = Read(project);
        if (map.Count == 0)
            return;
        var changed = 0;
        foreach (var key in map.Keys.ToList())
        {
            var text = French(map[key]);
            if (text != map[key])
            {
                map[key] = text;
                changed++;
            }
        }
        File.WriteAllText(FilePath(project), JsonSerializer.Serialize(map, Json).Replace("\r\n", "\n") + "\n");
        Console.WriteLine($"{Files.Rel(project)}: {changed} texts set in French typography");
    }

    public static void Update(string project)
    {
        var keys = Collect(project);
        if (keys.Count == 0 && !File.Exists(FilePath(project)))
            return;
        var existing = Read(project);
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in existing)
            merged[k] = Edges(k, v);   // kept even when unused: names translated where they're shown (plugin names, menus) are added by hand
        foreach (var k in keys)
            merged.TryAdd(k, "");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath(project))!);
        File.WriteAllText(FilePath(project), JsonSerializer.Serialize(merged, Json).Replace("\r\n", "\n") + "\n");
        var unused = merged.Keys.Count(k => !keys.Contains(k));
        Console.WriteLine($"{Files.Rel(project)}: {merged.Count} keys, {merged.Values.Count(v => v.Length == 0)} without French"
                          + (unused > 0 ? $", {unused} not in the code (kept)" : ""));
    }

    public static bool Check(string project)
    {
        var keys = Collect(project);
        var french = Read(project);
        var missing = keys.Where(k => !french.TryGetValue(k, out var v) || v.Length == 0).ToList();
        foreach (var k in missing)
            Console.WriteLine($"{Files.Rel(project)}: no French for \"{k}\"");
        // A translation must keep every {0}, {1}… of its English text.
        var broken = french.Where(p => p.Value.Length > 0 && Holes(p.Key) != Holes(p.Value)).ToList();
        foreach (var (k, v) in broken)
            Console.WriteLine($"{Files.Rel(project)}: \"{v}\" doesn't have the values of \"{k}\"");
        // Text glued to other text ("Saved: " + name) keeps its spaces at the edges.
        var edges = french.Where(p => p.Value.Length > 0 && Edges(p.Key, p.Value) != p.Value).ToList();
        foreach (var (k, v) in edges)
            Console.WriteLine($"{Files.Rel(project)}: \"{v}\" doesn't start or end with the spaces of \"{k}\" (run keys)");
        return missing.Count == 0 && broken.Count == 0 && edges.Count == 0;
    }

    // The translation with the same spaces at its start and end as the English text.
    private static string Edges(string english, string french) =>
        french.Length == 0 ? french
            : new string(' ', english.Length - english.TrimStart(' ').Length) + french.Trim(' ') + new string(' ', english.Length - english.TrimEnd(' ').Length);

    private static string Holes(string format) =>
        string.Join(",", Regex.Matches(format.Replace("{{", "").Replace("}}", ""), @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().Order());
}
