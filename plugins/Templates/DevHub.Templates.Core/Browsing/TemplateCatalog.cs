namespace DevHub.Templates.Core.Browsing;

/// <summary>How the template browser groups the list (the user switches between them).</summary>
public enum TemplateGrouping
{
    /// <summary>One group per category; a template with several categories is in each of them.</summary>
    Category,

    /// <summary>One group per language.</summary>
    Language,
}

/// <summary>What the browser shows: search text (every word must match), one library or all, one kind or all.</summary>
/// <param name="Library">A library's name, or null for every library.</param>
/// <param name="Kind">Project or item templates, or null for both.</param>
public sealed record TemplateFilter(string? Search = null, string? Library = null, TemplateKind? Kind = null);

/// <summary>One group of the browser's list.</summary>
public sealed record TemplateGroup(string Name, IReadOnlyList<TemplateEntry> Templates);

/// <summary>
/// The template browser's logic: every template of the libraries, filtered and grouped by
/// category or language. Broken templates (unreadable / invalid) stay visible in their own group,
/// so a typo in a template.json doesn't make a template silently vanish.
/// </summary>
public static class TemplateCatalog
{
    /// <summary>The group of templates without a category.</summary>
    public const string NoCategory = "Other";

    /// <summary>The group of templates that can't be used (with their problems).</summary>
    public const string BrokenGroup = "Problems";

    /// <summary>Every template of every library, in library order (a missing library folder is just empty).</summary>
    public static async Task<IReadOnlyList<TemplateEntry>> LoadAllAsync(IReadOnlyList<TemplateLibrary> libraries, CancellationToken ct = default)
    {
        var all = new List<TemplateEntry>();
        foreach (var library in libraries)
            all.AddRange(await TemplateStore.ListAsync(library, ct));
        return all;
    }

    public static IEnumerable<TemplateEntry> Filter(IEnumerable<TemplateEntry> templates, TemplateFilter filter)
    {
        var words = (filter.Search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return templates.Where(t =>
            (filter.Library is null || t.Library.Name == filter.Library)
            && (filter.Kind is null || t.Manifest is null || t.Manifest.Kind == filter.Kind)
            && words.All(w => SearchTexts(t).Any(s => s.Contains(w, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>
    /// Groups sorted by name ("Other" and "Problems" last), templates in a group sorted by name.
    /// The same template may be in several category groups.
    /// </summary>
    public static IReadOnlyList<TemplateGroup> Group(IEnumerable<TemplateEntry> templates, TemplateGrouping grouping)
    {
        var groups = new Dictionary<string, List<TemplateEntry>>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, TemplateEntry entry)
        {
            if (!groups.TryGetValue(name, out var list))
                groups[name] = list = [];
            if (!list.Contains(entry))
                list.Add(entry);
        }

        foreach (var template in templates)
        {
            if (!template.IsUsable)
            {
                Add(BrokenGroup, template);
                continue;
            }
            var manifest = template.Manifest!;
            if (grouping == TemplateGrouping.Language)
                Add(manifest.Language.Trim() is { Length: > 0 } language ? language : "Any", template);
            else if (manifest.Categories.Where(c => !string.IsNullOrWhiteSpace(c)).ToList() is { Count: > 0 } categories)
                categories.ForEach(c => Add(c.Trim(), template));
            else
                Add(NoCategory, template);
        }

        return groups
            .OrderBy(g => g.Key == BrokenGroup ? 2 : g.Key == NoCategory ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TemplateGroup(g.Key, g.Value.OrderBy(DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList()))
            .ToList();
    }

    /// <summary>The name to show: the manifest's, or the folder's for a broken template.</summary>
    public static string DisplayName(TemplateEntry template) =>
        template.Manifest?.Name is { Length: > 0 } name ? name : template.Id;

    private static IEnumerable<string> SearchTexts(TemplateEntry t) =>
        t.Manifest is { } m
            ? [m.Name, m.Id, m.Description ?? "", m.Language, .. m.Categories]
            : [t.Id];
}
