using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Templates.Core;
using DevHub.Templates.Core.Editing;
using DevHub.Templates.Core.Rendering;

namespace DevHub.ViewModels.Templates.Editor;

// The template editor's sections. Each one edits a piece of the manifest and builds it back with
// `original with { … }`, so fields DevHub doesn't know (Extra) survive a save. Any change to an
// edited property calls `changed` (the editor's "modified"), through OnPropertyChanged: no
// PropertyChanged subscriptions to undo.

/// <summary>A piece of the editor whose every property change is an edit.</summary>
public abstract class EditorPartViewModel(Action changed) : ViewModelBase
{
    protected Action Changed { get; } = changed;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        Changed();
    }
}

/// <summary>Name, id, description, language, categories, kind, project folder, binary extensions.</summary>
public sealed partial class GeneralPartViewModel : EditorPartViewModel
{
    public GeneralPartViewModel(TemplateManifest manifest, IReadOnlyList<string> languages, Action changed) : base(changed)
    {
        _name = manifest.Name;
        _id = manifest.Id;
        _description = manifest.Description ?? "";
        _language = manifest.Language;
        _categories = ListText.JoinList(manifest.Categories);
        _isProject = manifest.Kind == TemplateKind.Project;
        _rootFolder = manifest.RootFolder ?? "";
        _binaryExtensions = ListText.JoinList(manifest.BinaryExtensions);
        Languages = languages;
    }

    public IReadOnlyList<string> Languages { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _id;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _language;
    [ObservableProperty] private string _categories;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsItem))]
    private bool _isProject;

    /// <summary>The other radio button (a two-way binding can't negate).</summary>
    public bool IsItem
    {
        get => !IsProject;
        set => IsProject = !value;
    }

    /// <summary>Empty = the default, <c>{{Name}}</c>.</summary>
    [ObservableProperty] private string _rootFolder;
    [ObservableProperty] private string _binaryExtensions;

    public string RootFolderPlaceholder => TemplateManifest.DefaultRootFolder;

    public TemplateManifest Build(TemplateManifest manifest) => manifest with
    {
        Name = Name.Trim(),
        Id = Id.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        Language = string.IsNullOrWhiteSpace(Language) ? "Any" : Language.Trim(),
        Categories = ListText.SplitList(Categories),
        Kind = IsProject ? TemplateKind.Project : TemplateKind.Item,
        RootFolder = string.IsNullOrWhiteSpace(RootFolder) || RootFolder.Trim() == TemplateManifest.DefaultRootFolder ? null : RootFolder.Trim(),
        BinaryExtensions = ListText.SplitList(BinaryExtensions),
    };
}

/// <summary>
/// A list section (variables, steps, post-create commands): the items, the selected one's details,
/// Add / Remove / Move Up / Move Down. Selecting isn't an edit; changing the list is.
/// </summary>
public abstract partial class ListPartViewModel<TItem> : ViewModelBase where TItem : class
{
    private readonly Action _changed;

    protected ListPartViewModel(IEnumerable<TItem> items, Action changed)
    {
        _changed = changed;
        foreach (var item in items)
            Items.Add(item);
        _selected = Items.FirstOrDefault();
        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(Remove, () => Selected is not null);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => Selected is not null && Items.IndexOf(Selected) > 0);
        MoveDownCommand = new RelayCommand(() => Move(1), () => Selected is not null && Items.IndexOf(Selected) < Items.Count - 1);
    }

    public ObservableCollection<TItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private TItem? _selected;

    public bool HasSelection => Selected is not null;
    public bool IsEmpty => Items.Count == 0;

    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }

    /// <summary>A new item with sensible defaults (it's selected after adding).</summary>
    protected abstract TItem CreateItem();

    partial void OnSelectedChanged(TItem? value) => UpdateCommands();

    private void Add()
    {
        var item = CreateItem();
        Items.Insert(Selected is null ? Items.Count : Items.IndexOf(Selected) + 1, item);
        Selected = item;
        ListChanged();
    }

    private void Remove()
    {
        if (Selected is not { } item)
            return;
        var index = Items.IndexOf(item);
        Items.Remove(item);
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        ListChanged();
    }

    private void Move(int direction)
    {
        if (Selected is not { } item)
            return;
        var index = Items.IndexOf(item);
        Items.Move(index, index + direction);
        Selected = item;
        ListChanged();
    }

    private void ListChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        UpdateCommands();
        _changed();
    }

    private void UpdateCommands()
    {
        RemoveCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }
}

public sealed class VariablesPartViewModel(TemplateManifest manifest, Action changed)
    : ListPartViewModel<VariableItemViewModel>(manifest.Variables.Select(v => new VariableItemViewModel(v, changed)), changed)
{
    private readonly Action _changed = changed;

    /// <summary>Names of the bool variables (the post-create "when" choices).</summary>
    public IReadOnlyList<string> BoolNames => Items.Where(i => i.Type == VariableType.Bool).Select(i => i.Name.Trim()).ToList();

    public IReadOnlyList<string> Names => Items.Select(i => i.Name.Trim()).Where(n => n.Length > 0).ToList();

    protected override VariableItemViewModel CreateItem()
    {
        var name = "Variable";
        for (var n = 2; Items.Any(i => i.Name == name); n++)
            name = "Variable" + n;
        return new VariableItemViewModel(new TemplateVariable { Name = name }, _changed);
    }

    public IReadOnlyList<TemplateVariable> Build() => Items.Select(i => i.Build()).ToList();
}

/// <summary>One variable: name (used in placeholders), label, type, default, rules.</summary>
public sealed partial class VariableItemViewModel : EditorPartViewModel
{
    private readonly TemplateVariable _original;

    public VariableItemViewModel(TemplateVariable variable, Action changed) : base(changed)
    {
        _original = variable;
        _name = variable.Name;
        _label = variable.Label ?? "";
        _type = variable.Type;
        _defaultText = variable.Type == VariableType.Bool ? "" : variable.DefaultText;
        _defaultBool = variable.DefaultText == "true";
        _required = variable.Required;
        _pattern = variable.Pattern ?? "";
        _choices = ListText.JoinList(variable.Choices);
    }

    public static IReadOnlyList<VariableType> Types { get; } = Enum.GetValues<VariableType>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder))]
    private string _name;

    [ObservableProperty] private string _label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBool), nameof(IsChoice), nameof(IsText), nameof(TypeText))]
    private VariableType _type;

    [ObservableProperty] private string _defaultText;
    [ObservableProperty] private bool _defaultBool;
    [ObservableProperty] private bool _required;
    [ObservableProperty] private string _pattern;
    [ObservableProperty] private string _choices;

    public bool IsBool => Type == VariableType.Bool;
    public bool IsChoice => Type == VariableType.Choice;
    public bool IsText => Type == VariableType.String;
    public string TypeText => Type.ToString().ToLowerInvariant();

    /// <summary>How to use it: <c>{{Name}}</c>.</summary>
    public string Placeholder => "{{" + Name.Trim() + "}}";

    public TemplateVariable Build() => _original with
    {
        Name = Name.Trim(),
        Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(),
        Type = Type,
        Default = Type == VariableType.Bool
            ? (DefaultBool ? JsonSerializer.SerializeToElement(true) : null)
            : DefaultText.Length == 0 ? null : JsonSerializer.SerializeToElement(DefaultText),
        Required = Required,
        Pattern = Type == VariableType.String && !string.IsNullOrWhiteSpace(Pattern) ? Pattern : null,
        Choices = Type == VariableType.Choice ? ListText.SplitList(Choices) : [],
    };

    public override string ToString() => Name;
}

/// <summary>The base command (project templates): the tool's own scaffold command, or none.</summary>
public sealed partial class CommandPartViewModel : EditorPartViewModel
{
    private readonly BaseCommand? _original;

    public CommandPartViewModel(BaseCommand? command, Action changed) : base(changed)
    {
        _original = command;
        _hasCommand = command is not null;
        _command = command?.Command ?? "";
        _args = ListText.JoinLines(command?.Args ?? []);
        _runIn = command?.RunIn ?? RunIn.Root;
    }

    public static IReadOnlyList<RunIn> RunIns { get; } = Enum.GetValues<RunIn>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private bool _hasCommand;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private string _command;

    /// <summary>One argument per line ("My App" on one line stays one argument).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private string _args;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunInText))]
    private RunIn _runIn;

    /// <summary>How it reads, placeholders not filled in yet.</summary>
    public string Preview => HasCommand && Command.Trim().Length > 0
        ? new RenderedCommand("", Command.Trim(), ListText.SplitLines(Args), ".").DisplayText
        : "";

    public string RunInText => RunIn == RunIn.Root
        ? "Runs inside the (empty) project folder, e.g. dotnet new console --output ."
        : "Runs in the folder above and must create the project folder itself, e.g. cargo new {{Name}}";

    public BaseCommand? Build() => HasCommand && Command.Trim().Length > 0
        ? (_original ?? new BaseCommand()) with { Command = Command.Trim(), Args = ListText.SplitLines(Args), RunIn = RunIn }
        : null;
}

public sealed class StepsPartViewModel(TemplateManifest manifest, Action changed)
    : ListPartViewModel<StepItemViewModel>(manifest.Steps.Select(s => new StepItemViewModel(s, changed)), changed)
{
    private readonly Action _changed = changed;

    protected override StepItemViewModel CreateItem() =>
        new(new TemplateStep { Op = StepOp.Move, From = "*.cs", To = "src/{{Name}}/" }, _changed);

    public IReadOnlyList<TemplateStep> Build() => Items.Select(i => i.Build()).ToList();
}

/// <summary>One rearranging step (§6): which fields it uses depends on the operation.</summary>
public sealed partial class StepItemViewModel : EditorPartViewModel
{
    private readonly TemplateStep _original;

    public StepItemViewModel(TemplateStep step, Action changed) : base(changed)
    {
        _original = step;
        _op = step.Op;
        _path = step.Path ?? "";
        _from = step.From ?? "";
        _to = step.To ?? "";
    }

    public static IReadOnlyList<StepOp> Ops { get; } = Enum.GetValues<StepOp>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesPath), nameof(UsesFromTo), nameof(Summary), nameof(Help), nameof(ToLabel))]
    private StepOp _op;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _from;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _to;

    public bool UsesPath => Op is StepOp.CreateFolder or StepOp.Delete;
    public bool UsesFromTo => !UsesPath;
    public string ToLabel => Op == StepOp.Rename ? "New name" : "To";

    public string Summary => Op switch
    {
        StepOp.CreateFolder => $"create folder {Path}",
        StepOp.Delete => $"delete {Path}",
        StepOp.Rename => $"rename {From} → {To}",
        _ => $"move {From} → {To}",
    };

    public string Help => Op switch
    {
        StepOp.CreateFolder => "Creates the folder (and its parents).",
        StepOp.Delete => "Deletes files or folders. A path, or a glob (*, **, ?): a glob matching nothing is fine.",
        StepOp.Rename => "Renames one file or folder in place. \"New name\" is just the name, not a path.",
        _ => "Moves a path or everything a glob matches. \"To\" ending in / is a folder (names are kept); otherwise it's the new path of one item.",
    };

    public TemplateStep Build() => _original with
    {
        Op = Op,
        Path = UsesPath ? Path.Trim() : null,
        From = UsesFromTo ? From.Trim() : null,
        To = UsesFromTo ? To.Trim() : null,
    };

    public override string ToString() => Summary;
}

public sealed class PostCreatePartViewModel(TemplateManifest manifest, Func<IReadOnlyList<string>> boolVariables, Action changed)
    : ListPartViewModel<PostCreateItemViewModel>(manifest.PostCreate.Select(c => new PostCreateItemViewModel(c, boolVariables, changed)), changed)
{
    private readonly Action _changed = changed;
    private readonly Func<IReadOnlyList<string>> _boolVariables = boolVariables;

    protected override PostCreateItemViewModel CreateItem() =>
        new(new PostCreateCommand { Name = "Initialize git", Command = "git", Args = ["init"] }, _boolVariables, _changed);

    public IReadOnlyList<PostCreateCommand> Build() => Items.Select(i => i.Build()).ToList();
}

/// <summary>One post-create command: name, command, arguments, folder, condition, keep going on failure.</summary>
public sealed partial class PostCreateItemViewModel : EditorPartViewModel
{
    public const string Always = "(always)";

    private readonly PostCreateCommand _original;
    private readonly Func<IReadOnlyList<string>> _boolVariables;

    public PostCreateItemViewModel(PostCreateCommand command, Func<IReadOnlyList<string>> boolVariables, Action changed) : base(changed)
    {
        _original = command;
        _boolVariables = boolVariables;
        _name = command.Name;
        _command = command.Command;
        _args = ListText.JoinLines(command.Args);
        _workingDirectory = command.WorkingDirectory;
        _when = command.When ?? Always;
        _continueOnError = command.ContinueOnError;
    }

    [ObservableProperty] private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private string _command;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private string _args;

    [ObservableProperty] private string _workingDirectory;

    /// <summary>A bool variable's name, or <see cref="Always"/>.</summary>
    [ObservableProperty] private string _when;

    [ObservableProperty] private bool _continueOnError;

    /// <summary>"(always)" and the template's bool variables (read when the section is shown).</summary>
    public IReadOnlyList<string> WhenChoices =>
        [Always, .. _boolVariables(), .. When != Always && !_boolVariables().Contains(When) ? [When] : Array.Empty<string>()];

    public string Preview => Command.Trim().Length > 0
        ? new RenderedCommand("", Command.Trim(), ListText.SplitLines(Args), ".").DisplayText
        : "";

    public PostCreateCommand Build() => _original with
    {
        Name = Name.Trim(),
        Command = Command.Trim(),
        Args = ListText.SplitLines(Args),
        WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? "." : WorkingDirectory.Trim(),
        When = When == Always || string.IsNullOrWhiteSpace(When) ? null : When,
        ContinueOnError = ContinueOnError,
    };

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Preview : Name;
}

/// <summary>
/// template.json as text. Shown from the forms when the section opens; typing in it is an edit,
/// applied to the forms when you leave the section (or on Save).
/// </summary>
public sealed partial class JsonPartViewModel(bool isReadOnly, Action changed) : ViewModelBase
{
    public bool IsReadOnly { get; } = isReadOnly;

    [ObservableProperty] private string _text = "";

    /// <summary>Typed into since it was last shown from the forms.</summary>
    public bool IsEdited { get; private set; }

    /// <summary>Why the text couldn't be applied, or null.</summary>
    [ObservableProperty] private string? _error;

    private bool _showing;

    /// <summary>Shows the manifest (not an edit).</summary>
    public void Show(string json)
    {
        _showing = true;
        Text = json;
        _showing = false;
        IsEdited = false;
        Error = null;
    }

    partial void OnTextChanged(string value)
    {
        if (_showing)
            return;
        IsEdited = true;
        changed();
    }

    /// <summary>The text was applied to the forms (or dropped).</summary>
    public void MarkApplied() => IsEdited = false;
}
