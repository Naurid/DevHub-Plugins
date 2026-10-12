using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Integration;
using DevHub.Services;
using DevHub.Templates.Core;
using DevHub.Templates.Core.Editing;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Templates.Editor;

/// <summary>
/// The template's <c>content/</c> folder as a tree. New File / New Folder / Rename / Delete change
/// the disk right away (not on Save). Files are edited in your external editor (Open, or
/// double-click): the Open With editor of Settings when the Projects plugin offers one, else the
/// program Windows associates with the file.
/// </summary>
public sealed partial class FilesPartViewModel : ViewModelBase
{
    private readonly Func<TemplateEntry> _template;
    private readonly Func<IReadOnlyList<string>> _variables;
    private readonly TemplatesService _templates;
    private readonly IDialogService _dialogs;
    private readonly IStatusService _status;
    private readonly Func<IProjectFileEditor?> _editor;
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="template">The template as it is now (its folder moves when its id changes).</param>
    /// <param name="variables">The variables' names, for the placeholder hint.</param>
    public FilesPartViewModel(Func<TemplateEntry> template, Func<IReadOnlyList<string>> variables, TemplatesService templates,
        IDialogService dialogs, IStatusService status, Func<IProjectFileEditor?> editor)
    {
        _template = template;
        _variables = variables;
        _templates = templates;
        _dialogs = dialogs;
        _status = status;
        _editor = editor;
        Refresh();
    }

    public bool IsReadOnly => _template().Library.IsReadOnly;
    public bool CanEdit => !IsReadOnly;

    public ObservableCollection<ContentNodeViewModel> Nodes { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(DeleteCommand), nameof(OpenCommand))]
    private ContentNodeViewModel? _selectedNode;

    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string? _error;

    /// <summary>"Placeholders: {{Name}} {{Git}} · built-in: {{Year}} …"</summary>
    public string PlaceholderHint =>
        "Placeholders you can use in file contents and names: "
        + string.Join(" ", _variables().Select(v => "{{" + v + "}}"))
        + "  ·  built-in: {{Year}} {{Date}} {{Author}} {{Guid1}}…{{Guid9}}"
        + "  ·  transforms: {{Name:kebab}} in files, {{Name.kebab}} in file names (pascal, camel, kebab, snake, upper, lower).";

    public bool CanOpenInVsCode => _templates.VsCodePath is not null;

    /// <summary>Reads content/ again (after changes outside DevHub too); folders keep their open / closed state.</summary>
    [RelayCommand]
    public void Refresh()
    {
        var select = SelectedNode?.Path;
        var byPath = new Dictionary<string, ContentNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<ContentNodeViewModel>();
        foreach (var item in TemplateContent.List(_template()))
        {
            var node = new ContentNodeViewModel(item, !_collapsed.Contains(item.Path), OnExpandedChanged);
            byPath[item.Path] = node;
            if (item.Parent.Length > 0 && byPath.TryGetValue(item.Parent, out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }
        foreach (var node in byPath.Values)
            node.SortChildren();

        Nodes.Clear();
        foreach (var root in ContentNodeViewModel.Sorted(roots))
            Nodes.Add(root);
        IsEmpty = Nodes.Count == 0;
        SelectedNode = select is null ? null : byPath.GetValueOrDefault(select);
        OnPropertyChanged(nameof(PlaceholderHint));
    }

    private string SelectedFolder => SelectedNode is { } node ? node.IsFolder ? node.Path : node.Item.Parent : "";

    [RelayCommand]
    private async Task NewFileAsync()
    {
        var folder = SelectedFolder;
        var dialog = new NameDialogViewModel("New File", "File name (placeholders allowed: {{Name}}.cs)", "", "Create", Where(folder));
        if (await _dialogs.ShowDialogAsync(dialog))
            Do(TemplateContent.CreateFile(_template(), folder, dialog.Name), Join(folder, dialog.Name.Trim()));
    }

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        var folder = SelectedFolder;
        var dialog = new NameDialogViewModel("New Folder", "Folder name", "", "Create", Where(folder));
        if (await _dialogs.ShowDialogAsync(dialog))
            Do(TemplateContent.CreateFolder(_template(), folder, dialog.Name), Join(folder, dialog.Name.Trim()));
    }

    private bool HasSelection() => SelectedNode is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RenameAsync()
    {
        if (SelectedNode is not { } node)
            return;
        var dialog = new NameDialogViewModel("Rename", "New name", node.Name, "Rename", node.Path);
        if (await _dialogs.ShowDialogAsync(dialog) && dialog.Name.Trim() != node.Name)
            Do(TemplateContent.Rename(_template(), node.Path, dialog.Name), Join(node.Item.Parent, dialog.Name.Trim()));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedNode is not { } node)
            return;
        var what = node.IsFolder ? $"the folder {node.Path} and everything in it" : node.Path;
        var ok = await _dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Delete",
            $"Delete {what} from the template? It's deleted from disk now (not on Save), permanently.", "Delete", [node.Path]));
        if (ok)
            Do(TemplateContent.Delete(_template(), node.Path), null);
    }

    private bool CanOpen() => SelectedNode is { IsFolder: false };

    /// <summary>Opens the selected file in the external editor.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open()
    {
        if (SelectedNode is not { IsFolder: false } node || TemplateContent.FullPath(_template(), node.Path) is not { } file)
            return;
        try
        {
            if (_editor() is { } editor)
                editor.OpenFile(_template().Folder, file, null);
            else
                TemplatesService.OpenWithDefaultProgram(file);
        }
        catch (Exception ex)
        {
            _status.Show($"Couldn't open {node.Path}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ShowInExplorer() => TemplatesService.Reveal(_template().ContentFolder);

    [RelayCommand]
    private void OpenInVsCode() => _templates.OpenInVsCode(_template().Folder);

    private void Do(string? error, string? select)
    {
        Error = error;
        if (error is not null)
            return;
        Refresh();
        if (select is not null)
            SelectedNode = Find(Nodes, select);
    }

    private static ContentNodeViewModel? Find(IEnumerable<ContentNodeViewModel> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase))
                return node;
            if (Find(node.Children, path) is { } found)
                return found;
        }
        return null;
    }

    private void OnExpandedChanged(ContentNodeViewModel node)
    {
        if (node.IsExpanded)
            _collapsed.Remove(node.Path);
        else
            _collapsed.Add(node.Path);
    }

    private static string Where(string folder) => folder.Length == 0 ? "In content/" : "In content/" + folder;

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;
}

/// <summary>A file or folder of content/.</summary>
public sealed partial class ContentNodeViewModel : ViewModelBase
{
    private readonly Action<ContentNodeViewModel> _expandedChanged;

    public ContentNodeViewModel(ContentItem item, bool isExpanded, Action<ContentNodeViewModel> expandedChanged)
    {
        Item = item;
        _isExpanded = isExpanded;
        _expandedChanged = expandedChanged;
    }

    public ContentItem Item { get; }
    public string Name => Item.Name;
    public string Path => Item.Path;
    public bool IsFolder => Item.IsFolder;

    /// <summary>The name has a placeholder: it's renamed in every project.</summary>
    public bool HasPlaceholder => Name.Contains("{{", StringComparison.Ordinal);

    public ObservableCollection<ContentNodeViewModel> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => _expandedChanged(this);

    public void SortChildren()
    {
        var sorted = Sorted(Children).ToList();
        Children.Clear();
        foreach (var child in sorted)
            Children.Add(child);
    }

    /// <summary>Folders first, then files, each by name.</summary>
    public static IEnumerable<ContentNodeViewModel> Sorted(IEnumerable<ContentNodeViewModel> nodes) =>
        nodes.OrderBy(n => n.IsFolder ? 0 : 1).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase);

    public override string ToString() => IsFolder ? $"{Name} folder" : Name;
}
