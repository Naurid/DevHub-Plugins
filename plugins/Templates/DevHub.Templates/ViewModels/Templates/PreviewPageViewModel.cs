using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Services;
using DevHub.Templates.Core;
using DevHub.Templates.Core.Generation;
using DevHub.Templates.Core.Rendering;

namespace DevHub.ViewModels.Templates;

/// <summary>
/// Step 3: the project as it will be (still in the staging folder), as a tree that says where
/// each file came from. New Folder / Rename / Move / Delete change it for this project only;
/// Save to Template appends those edits to the template's steps so the next project gets them
/// too. Create writes it into the target folder.
/// </summary>
/// <remarks>
/// For an item template (Add Files) the tree is the folder as it will be: only what changes is shown
/// unless "Show unchanged files" is on; files the template would overwrite are listed with an
/// Overwrite choice each; your files that move or get deleted are spelled out; the post-create
/// commands are listed before Apply (they run only after it).
/// </remarks>
public sealed partial class PreviewPageViewModel : ViewModelBase, Controls.ITreeDropHandler
{
    private readonly IStagedResult _staged;
    private readonly StagedItem? _item;
    private readonly Dictionary<string, bool> _overwrite = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDialogService _dialogs;
    private readonly Func<IReadOnlyList<TemplateStep>, Task<SaveOutcome>> _save;
    private readonly Func<Task> _create;
    private readonly Action _back;
    private readonly string _commandName;
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private int _savedEdits;

    /// <param name="commandName">The base command ("dotnet"), to say which files it made.</param>
    /// <param name="saveIsCopy">The template is read-only: saving makes a copy in the personal library.</param>
    /// <param name="save">Saves the given (new) edits to the template; returns what happened.</param>
    /// <param name="commands">Item templates: the post-create commands that will run after Apply (listed here, since there's no Commands step).</param>
    public PreviewPageViewModel(IStagedResult staged, string? commandName, bool raw, bool saveIsCopy, IDialogService dialogs,
        Func<IReadOnlyList<TemplateStep>, Task<SaveOutcome>> save, Func<Task> create, Action back,
        IReadOnlyList<RenderedCommand>? commands = null)
    {
        _staged = staged;
        _item = staged as StagedItem;
        Commands = (commands ?? []).Select(c => new CommandRowViewModel(c)).ToList();
        _dialogs = dialogs;
        _save = save;
        _create = create;
        _back = back;
        _commandName = commandName ?? "the command";
        IsRaw = raw;
        SaveText = saveIsCopy ? "Save to a Copy in Personal" : "Save to Template";
        Rebuild(null);
    }

    public string TargetPath => _staged.TargetPath;

    public ObservableCollection<PreviewNodeViewModel> Nodes { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(MoveCommand), nameof(DeleteCommand))]
    private PreviewNodeViewModel? _selectedNode;

    /// <summary>"14 files: 4 from the template, 10 from dotnet"</summary>
    [ObservableProperty] private string _summary = "";

    /// <summary>Why the last edit or Create didn't work, or null.</summary>
    [ObservableProperty] private string? _error;

    /// <summary>What the last action did ("Saved 2 steps to Personal/dotnet-console.").</summary>
    [ObservableProperty] private string? _message;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(BackCommand), nameof(SaveToTemplateCommand), nameof(UndoCommand))]
    private bool _isBusy;

    public bool IsRaw { get; }
    public string SaveText { get; }

    /// <summary>Add Files (an item template on an existing folder), not New Project.</summary>
    public bool IsItem => _item is not null;

    public string Heading => IsItem ? $"Changes to {Path.GetFileName(TargetPath)}" : "Preview";
    public string TargetText => IsItem ? $"In {TargetPath}" : $"Will be created as {TargetPath}";
    public string CreateText => IsItem ? "Apply" : "Create";

    /// <summary>Item templates: the files the template would overwrite, each with its Overwrite choice.</summary>
    public ObservableCollection<ConflictRowViewModel> Conflicts { get; } = [];
    public bool HasConflicts => Conflicts.Count > 0;

    /// <summary>Item templates: your files that Apply deletes (a step or your edit deleted them).</summary>
    [ObservableProperty] private IReadOnlyList<string> _deleted = [];
    public bool HasDeleted => Deleted.Count > 0;

    /// <summary>Item templates: the post-create commands that run after Apply.</summary>
    public IReadOnlyList<CommandRowViewModel> Commands { get; }
    public bool HasCommands => Commands.Count > 0;

    /// <summary>Item templates: show the folder's untouched files too (off: only what changes).</summary>
    [ObservableProperty] private bool _showUnchanged;

    /// <summary>The conflicts to overwrite (Apply passes these on).</summary>
    public IReadOnlySet<string> Overwrite => _overwrite.Where(o => o.Value).Select(o => o.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    partial void OnDeletedChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasDeleted));

    partial void OnShowUnchangedChanged(bool value) => Rebuild(SelectedNode?.Path);

    public string SaveToolTip => IsRaw
        ? "A raw project skips the template's files and steps, so its edits can't become steps of the template."
        : "Add these edits to the template's steps, so the next project is arranged this way too.";

    private int UnsavedEdits => _staged.Edits.Count - _savedEdits;

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        // In the selected folder (or the selected file's folder), else at the top.
        var parent = SelectedNode is { } node ? node.IsFolder ? node.Path : node.Parent : "";
        var dialog = new NameDialogViewModel("New Folder", "Folder name", "", "Create",
            parent.Length == 0 ? "In the project folder" : "In " + parent);
        if (await _dialogs.ShowDialogAsync(dialog))
            Edit(new TemplateStep { Op = StepOp.CreateFolder, Path = Join(parent, dialog.Name) }, Join(parent, dialog.Name));
    }

    private bool HasSelection() => SelectedNode is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RenameAsync()
    {
        if (SelectedNode is not { } node)
            return;
        var dialog = new NameDialogViewModel("Rename", "New name", node.Name, "Rename", node.Path);
        if (await _dialogs.ShowDialogAsync(dialog) && dialog.Name != node.Name)
            Edit(new TemplateStep { Op = StepOp.Rename, From = node.Path, To = dialog.Name }, Join(node.Parent, dialog.Name));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task MoveAsync()
    {
        if (SelectedNode is not { } node)
            return;
        // Every folder it can go into: not where it is, not itself or a folder inside it.
        var folders = _staged.Tree.Entries.Where(e => e.IsFolder).Select(e => e.Path)
            .Prepend("")
            .Where(f => !f.Equals(node.Parent, StringComparison.OrdinalIgnoreCase))
            .Where(f => !node.IsFolder || !(f + "/").StartsWith(node.Path + "/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var dialog = new MoveDialogViewModel(node.Path, folders);
        if (!await _dialogs.ShowDialogAsync(dialog) || dialog.SelectedFolder is not { } target)
            return;
        // A plain path as "to" (not "folder/"): moves this one entry to exactly that path.
        var to = Join(target.Path, node.Name);
        Edit(new TemplateStep { Op = StepOp.Move, From = node.Path, To = to }, to);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        if (SelectedNode is not { } node)
            return;
        Edit(new TemplateStep { Op = StepOp.Delete, Path = node.Path }, node.Parent.Length == 0 ? null : node.Parent);
    }

    private bool CanSave() => !IsRaw && !IsBusy && UnsavedEdits > 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveToTemplateAsync()
    {
        IsBusy = true;
        try
        {
            var edits = _staged.Edits.Skip(_savedEdits).ToList();
            var outcome = await _save(edits);
            Message = outcome.Message;
            if (outcome.Saved)
                _savedEdits += edits.Count;
        }
        finally
        {
            IsBusy = false;
            SaveToTemplateCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanAct() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task CreateAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            await _create();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Back() => _back();

    /// <summary>Shows a problem from Create (e.g. the folder isn't empty any more).</summary>
    public void ShowError(string error) => Error = error;

    private void Edit(TemplateStep edit, string? select)
    {
        Message = null;
        Error = _staged.ApplyEdit(edit);
        if (Error is null)
            Rebuild(select);
        SaveToTemplateCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
    }

    private bool CanUndo() => _staged.CanUndo && !IsBusy;

    /// <summary>Takes back the last edit (Ctrl+Z): the tree as it was before it.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_staged.UndoEdit() is not { } undone)
            return;
        Error = null;
        var wasSaved = _savedEdits > _staged.Edits.Count;
        _savedEdits = Math.Min(_savedEdits, _staged.Edits.Count);
        Message = wasSaved
            ? "Undone here. It was already saved to the template, so it's still one of its steps (remove it in the template's editor)."
            : $"Undone: {Describe(undone)}.";
        Rebuild(SelectedNode?.Path);
        SaveToTemplateCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
    }

    private static string Describe(TemplateStep edit) => edit.Op switch
    {
        StepOp.CreateFolder => $"new folder {edit.Path}",
        StepOp.Delete => $"delete {edit.Path}",
        StepOp.Rename => $"rename {edit.From} to {edit.To}",
        _ => $"move {edit.From} to {edit.To}",
    };

    // ── Drag and drop (TreeDragDrop): a row onto a folder (or a file: its folder), or onto the empty space (the top). ──

    /// <summary>The folder a drop on <paramref name="target"/> means: the folder itself, a file's folder, or the top ("").</summary>
    private static string DropFolder(PreviewNodeViewModel? target) => target is null ? "" : target.IsFolder ? target.Path : target.Parent;

    public bool CanDrop(object source, object? target)
    {
        if (IsBusy || source is not PreviewNodeViewModel node || target is not (null or PreviewNodeViewModel))
            return false;
        var folder = DropFolder(target as PreviewNodeViewModel);
        if (folder.Equals(node.Parent, StringComparison.OrdinalIgnoreCase))
            return false; // already there
        // A folder can't go into itself or a folder inside it.
        return !node.IsFolder || !(folder + "/").StartsWith(node.Path + "/", StringComparison.OrdinalIgnoreCase);
    }

    public void Drop(object source, object? target)
    {
        if (!CanDrop(source, target))
            return;
        var node = (PreviewNodeViewModel)source;
        var to = Join(DropFolder(target as PreviewNodeViewModel), node.Name);
        // The same edit as Move to…: one entry to exactly that path (so Save to Template keeps it).
        Edit(new TemplateStep { Op = StepOp.Move, From = node.Path, To = to }, to);
    }

    /// <summary>Builds the tree from the staged result; folders keep their open / closed state.</summary>
    private void Rebuild(string? select)
    {
        var conflicts = _item?.Conflicts.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var entries = _staged.Tree.Entries;
        if (IsItem && !ShowUnchanged)
            entries = OnlyChanges(entries);

        var byPath = new Dictionary<string, PreviewNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<PreviewNodeViewModel>();
        foreach (var entry in entries) // sorted by path: a folder comes before what's in it
        {
            var node = new PreviewNodeViewModel(entry, _commandName, !_collapsed.Contains(entry.Path), OnExpandedChanged,
                conflicts.Contains(entry.Path));
            byPath[entry.Path] = node;
            if (entry.Parent.Length > 0 && byPath.TryGetValue(entry.Parent, out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }
        foreach (var node in byPath.Values)
            node.SortChildren();

        Nodes.Clear();
        foreach (var root in PreviewNodeViewModel.Sorted(roots))
            Nodes.Add(root);
        SelectedNode = select is null ? null : byPath.GetValueOrDefault(select);

        if (_item is not null)
        {
            // The conflict list follows the tree (an edit can remove a conflicting file); choices are kept.
            foreach (var row in Conflicts)
                row.Dispose();
            Conflicts.Clear();
            foreach (var path in conflicts.Order(StringComparer.OrdinalIgnoreCase))
                Conflicts.Add(new ConflictRowViewModel(path, _overwrite.GetValueOrDefault(path), OnOverwriteChanged));
            OnPropertyChanged(nameof(HasConflicts));
            UpdateItemSummary();
            return;
        }

        var files = _staged.Tree.Entries.Where(e => !e.IsFolder).ToList();
        var fromTemplate = files.Count(f => f.Origin == EntryOrigin.Template);
        var moved = files.Count(f => f.IsMoved);
        Summary = $"{Count(files.Count, "file")}: {fromTemplate} from the template, {files.Count - fromTemplate} from {_commandName}"
                  + (moved > 0 ? $"; {moved} moved" : "")
                  + (_staged.Edits.Count > 0 ? $"; {Count(_staged.Edits.Count, "edit")} of yours" : "");
    }

    /// <summary>What changes (new, moved, a template's folder) and the folders they're in; the rest of the folder is left out.</summary>
    private static IReadOnlyList<TreeEntry> OnlyChanges(IReadOnlyList<TreeEntry> entries)
    {
        var changed = entries.Where(e => e.Origin != EntryOrigin.Existing || e.IsMoved).ToHashSet();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in changed)
        {
            for (var parent = entry.Parent; parent.Length > 0; parent = parent.LastIndexOf('/') is var s and >= 0 ? parent[..s] : "")
                folders.Add(parent);
        }
        return entries.Where(e => changed.Contains(e) || folders.Contains(e.Path)).ToList();
    }

    private void OnOverwriteChanged(ConflictRowViewModel row)
    {
        _overwrite[row.Path] = row.Overwrite;
        UpdateItemSummary();
    }

    private void UpdateItemSummary()
    {
        var changes = _item!.Changes(Overwrite);
        Deleted = changes.Deleted;
        var parts = new List<string> { $"{Count(changes.Added.Count, "new file")}" };
        if (changes.Overwritten.Count > 0)
            parts.Add($"{changes.Overwritten.Count} overwritten");
        if (changes.Kept.Count > 0)
            parts.Add($"{Count(changes.Kept.Count, "file")} of yours kept instead of the template's");
        if (changes.Moved.Count > 0)
            parts.Add($"{Count(changes.Moved.Count, "file")} of yours moved");
        if (changes.Deleted.Count > 0)
            parts.Add($"{Count(changes.Deleted.Count, "file")} of yours deleted");
        Summary = string.Join("; ", parts) + (changes.IsEmpty ? ". Nothing would change." : ".");
    }

    private void OnExpandedChanged(PreviewNodeViewModel node)
    {
        if (node.IsExpanded)
            _collapsed.Remove(node.Path);
        else
            _collapsed.Add(node.Path);
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
}

/// <summary>A file the template would overwrite: off (the default) keeps yours.</summary>
public sealed partial class ConflictRowViewModel : ViewModelBase, IDisposable
{
    private Action<ConflictRowViewModel>? _changed;

    public ConflictRowViewModel(string path, bool overwrite, Action<ConflictRowViewModel> changed)
    {
        Path = path;
        _overwrite = overwrite;
        _changed = changed;
    }

    public string Path { get; }

    [ObservableProperty] private bool _overwrite;

    partial void OnOverwriteChanged(bool value) => _changed?.Invoke(this);

    /// <summary>The row is replaced (the list was rebuilt): stop reporting.</summary>
    public void Dispose() => _changed = null;

    public override string ToString() => Overwrite ? $"{Path}: overwrite" : $"{Path}: keep yours";
}

/// <summary>What Save to Template did: saved (the edits are now steps) or not, and a sentence about it.</summary>
public sealed record SaveOutcome(bool Saved, string Message);

/// <summary>A file or folder in the preview tree.</summary>
public sealed partial class PreviewNodeViewModel : ViewModelBase
{
    private readonly Action<PreviewNodeViewModel> _expandedChanged;

    /// <param name="isConflict">A template file that would replace a file already there (item templates).</param>
    public PreviewNodeViewModel(TreeEntry entry, string commandName, bool isExpanded, Action<PreviewNodeViewModel> expandedChanged,
        bool isConflict = false)
    {
        Entry = entry;
        IsConflict = isConflict;
        _expandedChanged = expandedChanged;
        _isExpanded = isExpanded;
        Origin = entry.Origin switch
        {
            EntryOrigin.Template => "template",
            EntryOrigin.Command => commandName,
            _ => "",
        };
    }

    public TreeEntry Entry { get; }
    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public string Parent => Entry.Parent;
    public bool IsFolder => Entry.IsFolder;
    public bool IsFile => !Entry.IsFolder;

    /// <summary>Where it came from: "template", or the base command's name.</summary>
    public string Origin { get; }

    public bool IsFromTemplate => Entry.Origin == EntryOrigin.Template && !IsFolder && !IsConflict;
    public bool IsConflict { get; }
    public bool IsMoved => Entry.IsMoved;
    public string MovedText => IsMoved ? "moved from " + Entry.OriginalPath : "";

    public ObservableCollection<PreviewNodeViewModel> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => _expandedChanged(this);

    public void SortChildren()
    {
        var sorted = Sorted(Children).ToList();
        Children.Clear();
        foreach (var child in sorted)
            Children.Add(child);
    }

    /// <summary>Folders first, then files, each by name (like Explorer).</summary>
    public static IEnumerable<PreviewNodeViewModel> Sorted(IEnumerable<PreviewNodeViewModel> nodes) =>
        nodes.OrderBy(n => n.IsFolder ? 0 : 1).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase);

    public override string ToString() => IsMoved ? $"{Name}, {MovedText}" : IsFolder ? $"{Name} folder" : Name;
}

/// <summary>Asks for one file or folder name (New Folder, Rename); refuses names Windows doesn't allow.</summary>
public sealed partial class NameDialogViewModel : Dialogs.DialogViewModelBase
{
    public NameDialogViewModel(string title, string label, string initial, string okText, string context)
    {
        Title = title;
        Label = label;
        OkText = okText;
        Context = context;
        _name = initial;
    }

    public override string Title { get; }
    public override string OkText { get; }
    public override bool FitsContent => true;
    public override double Width => 420;
    public override double MinWidth => 340;
    public override double MinHeight => 0;

    public string Label { get; }

    /// <summary>Where it is ("In src/App").</summary>
    public string Context { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string? _error;

    partial void OnNameChanged(string value) => Error = null;

    protected override Task<bool> ApplyChangesAsync()
    {
        Name = Name.Trim();
        Error = Name.Length == 0 ? "Enter a name." : PathRenderer.NameProblem(Name);
        return Task.FromResult(Error is null);
    }
}

/// <summary>Move: pick the folder the file or folder goes into.</summary>
public sealed partial class MoveDialogViewModel : Dialogs.DialogViewModelBase
{
    public MoveDialogViewModel(string what, IReadOnlyList<string> folders)
    {
        What = what;
        Folders = folders.Select(f => new FolderChoice(f)).ToList();
        _selectedFolder = Folders.FirstOrDefault();
    }

    public override string Title => "Move";
    public override string OkText => "Move";
    public override double Width => 460;
    public override double Height => 420;

    public string What { get; }
    public IReadOnlyList<FolderChoice> Folders { get; }

    [ObservableProperty] private FolderChoice? _selectedFolder;

    protected override Task<bool> ApplyChangesAsync() => Task.FromResult(SelectedFolder is not null);
}

/// <param name="Path">"" for the project folder itself.</param>
public sealed record FolderChoice(string Path)
{
    public string Text => Path.Length == 0 ? "(the project folder)" : Path;

    public override string ToString() => Text;
}
