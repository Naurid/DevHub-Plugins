using System.Text.RegularExpressions;
using DevHub.Templates.Core.Generation;
using DevHub.Templates.Core.Rendering;

namespace DevHub.Templates.Core.Importing;

/// <summary>
/// What import leaves out (§10), gitignore-style: <c>name</c> matches a file or folder of that name at
/// any depth, <c>name/</c> only a folder, a pattern with a slash inside (<c>src/gen/</c>,
/// <c>/build</c>) a path from the folder's top. Globs <c>*</c>, <c>**</c>, <c>?</c>; case-insensitive;
/// empty lines and <c>#</c> comments are skipped.
/// </summary>
public sealed class IgnoreRules
{
    /// <summary>The defaults the import page starts with (editable there).</summary>
    public static readonly IReadOnlyList<string> Defaults =
        [".git/", ".vs/", ".idea/", "bin/", "obj/", "node_modules/", "Library/", "Temp/", "Logs/", "dist/", "build/", "__pycache__/", "*.user"];

    private readonly List<(Glob Glob, bool FoldersOnly, bool Anchored)> _rules = [];

    public IgnoreRules(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var pattern = raw.Trim().Replace('\\', '/');
            if (pattern.Length == 0 || pattern.StartsWith('#'))
                continue;
            var foldersOnly = pattern.EndsWith('/');
            var anchored = pattern.TrimEnd('/').Contains('/'); // "/out" or "src/gen": from the top only
            pattern = pattern.Trim('/');
            if (pattern.Length == 0)
                continue;
            _rules.Add((new Glob(pattern), foldersOnly, anchored));
        }
    }

    /// <param name="path">"/"-separated, relative to the imported folder.</param>
    public bool IsIgnored(string path, bool isFolder)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return _rules.Any(r => (!r.FoldersOnly || isFolder) && r.Glob.IsMatch(r.Anchored ? path : name));
    }
}

/// <summary>A literal in the imported folder that becomes a variable ("LumberjackHollow" → Name).</summary>
public sealed record LiteralReplacement(string Literal, string Variable);

/// <summary>
/// Puts placeholders where the literals are, in text and in file names. A literal made of several
/// words is also found in its other spellings, each becoming the matching transform:
/// "LumberjackHollow" → <c>{{Name}}</c>, "lumberjack-hollow" → <c>{{Name:kebab}}</c>,
/// "lumberjack_hollow" → <c>{{Name:snake}}</c>… (in file names <c>{{Name.kebab}}</c>, since Windows
/// doesn't allow ':' there). A one-word literal is only found as typed, so "Game" doesn't turn
/// "gameObject" into a placeholder. Longest match first, in one pass.
/// </summary>
public sealed class LiteralReplacer
{
    private static readonly string[] Transforms = ["pascal", "camel", "kebab", "snake", "upper", "lower"];
    private readonly Regex? _pattern;
    private readonly Dictionary<string, (string Variable, string? Transform)> _variants = new(StringComparer.Ordinal);

    public LiteralReplacer(IEnumerable<LiteralReplacement> replacements)
    {
        foreach (var replacement in replacements.Where(r => r.Literal.Length >= 2 && r.Variable.Length > 0))
        {
            _variants.TryAdd(replacement.Literal, (replacement.Variable, null));
            if (CaseTransforms.Words(replacement.Literal).Count < 2)
                continue;
            foreach (var transform in Transforms)
            {
                if (CaseTransforms.Apply(transform, replacement.Literal) is { Length: >= 2 } variant)
                    _variants.TryAdd(variant, (replacement.Variable, transform));
            }
        }
        if (_variants.Count > 0)
            _pattern = new Regex(string.Join('|', _variants.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)));
    }

    /// <summary>The spellings looked for, and what each becomes in text.</summary>
    public IReadOnlyDictionary<string, string> Variants =>
        _variants.ToDictionary(v => v.Key, v => Token(v.Value.Variable, v.Value.Transform, ':'));

    public (string Text, int Hits) InText(string text) => Replace(text, ':');

    public (string Name, int Hits) InName(string name) => Replace(name, '.');

    private (string, int) Replace(string text, char separator)
    {
        if (_pattern is null)
            return (text, 0);
        var hits = 0;
        var result = _pattern.Replace(text, m =>
        {
            hits++;
            var (variable, transform) = _variants[m.Value];
            return Token(variable, transform, separator);
        });
        return (result, hits);
    }

    private static string Token(string variable, string? transform, char separator) =>
        transform is null ? "{{" + variable + "}}" : "{{" + variable + separator + transform + "}}";
}

/// <summary>One file or folder of the imported folder in the preview.</summary>
/// <param name="Path">Where it is ("/"-separated, relative).</param>
/// <param name="TargetPath">What it's called in the template (placeholders put in), or null when ignored.</param>
/// <param name="Hits">Placeholders put into its name and contents.</param>
public sealed record ImportEntry(string Path, bool IsFolder, bool IsIgnored, string? TargetPath, int Hits, bool IsBinary)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
    public string Parent => Path.LastIndexOf('/') is var slash and >= 0 ? Path[..slash] : "";
}

/// <summary>What an import would do: the entries, and problems that stop it (two files becoming one).</summary>
public sealed record ImportPreview(IReadOnlyList<ImportEntry> Entries, IReadOnlyList<string> Problems)
{
    public int FileCount => Entries.Count(e => !e.IsFolder && !e.IsIgnored);
    public int IgnoredCount => Entries.Count(e => e.IsIgnored);
    public int Hits => Entries.Sum(e => e.Hits);
}

/// <summary>
/// Turns an existing folder into a template (§10): everything not ignored is copied into the new
/// template's content/, literals replaced by placeholders in names and text (binary files copied as
/// they are). Text that would already render as a placeholder (a <c>{{Name}}</c> in a Handlebars file,
/// with Name a variable) is escaped as <c>\{{Name}}</c>, so the template produces it unchanged.
/// Links are skipped; ignored folders aren't looked into.
/// </summary>
public static partial class TemplateImporter
{
    public static async Task<ImportPreview> PreviewAsync(string folder, IgnoreRules ignore, LiteralReplacer replacer,
        CancellationToken ct = default)
    {
        var entries = new List<ImportEntry>();
        await WalkAsync(new DirectoryInfo(folder), "", "", ignore, replacer, entries, ct);

        var problems = entries.Where(e => e.TargetPath is not null)
            .GroupBy(e => e.TargetPath!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{string.Join(" and ", g.Select(e => e.Path))} would all become \"{g.Key}\".")
            .ToList();
        foreach (var entry in entries.Where(e => e.TargetPath is not null && e.Hits > 0))
        {
            if (entry.TargetPath!.Split('/').Select(PathRenderer.NameProblem).FirstOrDefault(p => p is not null) is { } problem)
                problems.Add($"{entry.Path}: {problem}");
        }
        return new ImportPreview(entries, problems);
    }

    /// <summary>
    /// Creates the template (<paramref name="manifest"/> in <paramref name="library"/>) and copies the
    /// folder into its content/. On any problem nothing is left behind (the new template folder is deleted).
    /// </summary>
    public static async Task<TemplateStoreResult> ImportAsync(string folder, IgnoreRules ignore, LiteralReplacer replacer,
        TemplateLibrary library, TemplateManifest manifest, CancellationToken ct = default)
    {
        var preview = await PreviewAsync(folder, ignore, replacer, ct);
        if (preview.Problems.Count > 0)
            return new TemplateStoreResult(null, preview.Problems);

        var created = await TemplateStore.CreateAsync(library, manifest, ct);
        if (created.Entry is not { } template)
            return created;

        var names = manifest.Variables.Select(v => v.Name).Concat(ManifestValidator.BuiltInVariables).ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (var entry in preview.Entries.Where(e => !e.IsIgnored))
            {
                ct.ThrowIfCancellationRequested();
                var target = SafePath.Resolve(template.ContentFolder, entry.TargetPath!)
                             ?? throw new InvalidOperationException($"{entry.TargetPath} leaves the template.");
                if (entry.IsFolder)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                var source = Path.Combine(folder, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                await ContentFiles.TransformAsync(source, target, text => replacer.InText(EscapeExisting(text, names)).Text,
                    manifest.BinaryExtensions, ct);
            }
        }
        catch
        {
            TemplateStore.Delete(template);
            throw;
        }
        return created;
    }

    /// <summary>Escapes text that would render as one of <paramref name="names"/>: <c>{{Name}}</c> → <c>\{{Name}}</c>.</summary>
    public static string EscapeExisting(string text, IReadOnlySet<string> names) =>
        !text.Contains("{{", StringComparison.Ordinal) ? text
            : ExistingToken().Replace(text, m => names.Contains(m.Groups[1].Value) ? "\\" + m.Value : m.Value);

    private static async Task WalkAsync(DirectoryInfo dir, string relative, string targetRelative, IgnoreRules ignore,
        LiteralReplacer replacer, List<ImportEntry> entries, CancellationToken ct)
    {
        foreach (var info in dir.EnumerateFileSystemInfos().OrderBy(i => i is FileInfo).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (info.LinkTarget is not null)
                continue;
            var isFolder = info is DirectoryInfo;
            var path = relative.Length == 0 ? info.Name : relative + "/" + info.Name;
            if (ignore.IsIgnored(path, isFolder))
            {
                entries.Add(new ImportEntry(path, isFolder, true, null, 0, false));
                continue;
            }

            var (name, nameHits) = replacer.InName(info.Name);
            var target = targetRelative.Length == 0 ? name : targetRelative + "/" + name;
            if (info is DirectoryInfo sub)
            {
                entries.Add(new ImportEntry(path, true, false, target, nameHits, false));
                await WalkAsync(sub, path, target, ignore, replacer, entries, ct);
                continue;
            }

            var text = await ContentFiles.ReadTextAsync(info.FullName, [], ct);
            var contentHits = text is null ? 0 : replacer.InText(text).Hits;
            entries.Add(new ImportEntry(path, false, false, target, nameHits + contentHits, text is null));
        }
    }

    [GeneratedRegex(@"(?<!\\)\{\{([A-Za-z][A-Za-z0-9]*)(?:[:.][A-Za-z]+)?\}\}")]
    private static partial Regex ExistingToken();
}
