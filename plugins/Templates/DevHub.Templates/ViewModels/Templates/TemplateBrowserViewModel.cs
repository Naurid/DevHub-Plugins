using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Services;
using DevHub.Templates.Core;
using DevHub.Templates.Core.Browsing;

namespace DevHub.ViewModels.Templates;

/// <summary>
/// The template list of New Project (left): search, library and kind filters, grouped by
/// category or language (<see cref="TemplateCatalog"/> does the work). Selecting a template
/// tells the owner (New Project).
/// </summary>
public sealed partial class TemplateBrowserViewModel : ViewModelBase
{
    public const string AllLibraries = "All libraries";
    private const string ProjectKind = "Project templates";
    private const string ItemKind = "Item templates";
    private const string AllKinds = "All templates";

    private readonly TemplatesService _templates;
    private readonly Action<TemplateEntry?> _selectionChanged;
    private IReadOnlyList<TemplateEntry> _all = [];

    /// <param name="selectionChanged">Told about the selected template (null: none), also when the same one was read again.</param>
    public TemplateBrowserViewModel(TemplatesService templates, Action<TemplateEntry?> selectionChanged)
    {
        _templates = templates;
        _selectionChanged = selectionChanged;
        _groupByLanguage = templates.GroupByLanguage;
    }

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _library = AllLibraries;
    [ObservableProperty] private string _kind = ProjectKind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupByCategory))]
    private bool _groupByLanguage;

    /// <summary>The other radio button of the grouping (a two-way binding can't negate).</summary>
    public bool GroupByCategory
    {
        get => !GroupByLanguage;
        set => GroupByLanguage = !value;
    }

    public ObservableCollection<string> Libraries { get; } = [AllLibraries];
    public IReadOnlyList<string> Kinds { get; } = [ProjectKind, ItemKind, AllKinds];

    public ObservableCollection<TemplateGroupViewModel> Groups { get; } = [];

    /// <summary>The tree's selection: a group header or a template row.</summary>
    [ObservableProperty] private object? _selectedNode;

    /// <summary>The selected template, or null.</summary>
    public TemplateEntry? Selected => (SelectedNode as TemplateRowViewModel)?.Template;

    /// <summary>False while a generation runs (switching templates would throw its work away).</summary>
    [ObservableProperty] private bool _isEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isLoading;

    /// <summary>Something is loaded (at all, before filtering): else the empty state shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _hasTemplates;

    /// <summary>No templates at all (after loading): the "add one" empty state.</summary>
    public bool ShowEmpty => !IsLoading && !HasTemplates;

    /// <summary>Templates exist, but the filters hide all of them.</summary>
    [ObservableProperty] private bool _nothingMatches;

    [ObservableProperty] private string? _loadError;

    /// <summary>Reads every library again; the selection stays on the same template (or <paramref name="select"/>).</summary>
    /// <param name="select">A template's <see cref="TemplateEntry.Reference"/> to select afterwards.</param>
    public async Task ReloadAsync(string? select = null)
    {
        var keep = select ?? Selected?.Reference;
        IsLoading = true;
        try
        {
            _all = await Task.Run(() => _templates.LoadAllAsync());
            LoadError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _all = [];
            LoadError = "Couldn't read the templates: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }

        var names = _all.Select(t => t.Library.Name).Distinct().ToList();
        foreach (var gone in Libraries.Skip(1).Except(names).ToList())
            Libraries.Remove(gone);
        foreach (var name in names.Where(n => !Libraries.Contains(n)))
            Libraries.Add(name);
        if (!Libraries.Contains(Library))
            Library = AllLibraries;

        HasTemplates = _all.Count > 0;
        Rebuild(keep);
    }

    [RelayCommand]
    private Task Reload() => ReloadAsync();

    [RelayCommand]
    private void OpenLibraryFolder() => TemplatesService.Reveal(_templates.PersonalLibrary.Root);

    partial void OnSearchChanged(string value) => Rebuild(Selected?.Reference);
    partial void OnLibraryChanged(string value) => Rebuild(Selected?.Reference);
    partial void OnKindChanged(string value) => Rebuild(Selected?.Reference);

    partial void OnGroupByLanguageChanged(bool value)
    {
        _templates.GroupByLanguage = value;
        Rebuild(Selected?.Reference);
    }

    partial void OnSelectedNodeChanged(object? value)
    {
        OnPropertyChanged(nameof(Selected));
        if (!_rebuilding)
            _selectionChanged(Selected);
    }

    // While the list is rebuilt the tree drops its selection (the rows are replaced); that isn't
    // the user choosing nothing, so the change is reported once, at the end, if the template changed.
    private bool _rebuilding;

    private void Rebuild(string? select)
    {
        var before = Selected;
        var filter = new TemplateFilter(Search,
            Library == AllLibraries ? null : Library,
            Kind switch { ProjectKind => TemplateKind.Project, ItemKind => TemplateKind.Item, _ => null });
        var groups = TemplateCatalog.Group(TemplateCatalog.Filter(_all, filter),
            GroupByLanguage ? TemplateGrouping.Language : TemplateGrouping.Category);

        _rebuilding = true;
        try
        {
            Groups.Clear();
            foreach (var group in groups)
                Groups.Add(new TemplateGroupViewModel(group, GroupByLanguage));
            NothingMatches = HasTemplates && Groups.Count == 0;

            // The same template again (its first row: with several categories it's in several groups).
            SelectedNode = Groups.SelectMany(g => g.Templates).FirstOrDefault(r => r.Template.Reference == select);
        }
        finally
        {
            _rebuilding = false;
        }
        if (!ReferenceEquals(Selected, before)) // another template, or the same one read again (Reload)
            _selectionChanged(Selected);
    }
}

/// <summary>A group of the list (a category or a language).</summary>
public sealed partial class TemplateGroupViewModel(TemplateGroup group, bool byLanguage) : ViewModelBase
{
    public string Name { get; } = group.Name;

    public IReadOnlyList<TemplateRowViewModel> Templates { get; } =
        group.Templates.Select(t => new TemplateRowViewModel(t, byLanguage)).ToList();

    public string CountText => Templates.Count.ToString();

    [ObservableProperty] private bool _isExpanded = true;

    public override string ToString() => $"{Name}, {Templates.Count} templates";
}

/// <summary>One template in the list.</summary>
/// <param name="byLanguage">The list is grouped by language, so the row shows the categories instead.</param>
public sealed class TemplateRowViewModel(TemplateEntry template, bool byLanguage) : ViewModelBase
{
    public TemplateEntry Template { get; } = template;

    public string Name => TemplateCatalog.DisplayName(Template);

    /// <summary>The detail on the right: the language, or the categories when grouped by language.</summary>
    public string Detail => Template.Manifest is not { } m ? ""
        : byLanguage ? string.Join(", ", m.Categories)
        : m.Language;

    public bool IsBroken => !Template.IsUsable;

    public bool IsItem => Template.Manifest?.Kind == TemplateKind.Item;

    public string ToolTip => IsBroken
        ? string.Join(Environment.NewLine, Template.Problems)
        : $"{Template.Manifest!.Description}{Environment.NewLine}{Template.Reference}".Trim();

    public override string ToString() => IsBroken ? $"{Name} (broken)" : Name;
}
