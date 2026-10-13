using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board;
using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using DevHub.Services;
using DevHub.Services.Board;

namespace DevHub.ViewModels.Dialogs;

/// <summary>An id with a name, for drop-downs (types, people, statuses, sprints…). Null id = "none".</summary>
public sealed record NamedItem(string? Id, string Name, string? Icon = null)
{
    public override string ToString() => Name;
}

/// <summary>A store kind New Board offers ("In DevHub", "In a folder", "On the repository's devhub/board branch").</summary>
public sealed record StoreChoice(string Kind, string Name, string Description, bool NeedsFolder)
{
    public override string ToString() => Name;
}

/// <summary>
/// New Board: project name and key, a preset, and where to keep it (in DevHub, in a folder, or a store another
/// plugin offers, like Board.Git's repository branch). Creates and opens it; <see cref="Created"/> is the result.
/// </summary>
public sealed partial class NewBoardDialogViewModel : DialogViewModelBase
{
    private readonly BoardService _boards;
    private bool _keyEdited;

    public NewBoardDialogViewModel(BoardService boards, BoardLocation? suggested = null, IDialogService? dialogs = null)
    {
        _boards = boards;
        _dialogs = dialogs;
        Stores.Add(new StoreChoice("devhub", "In DevHub", "Kept in DevHub's own folder; nothing else needed.", false));
        foreach (var provider in boards.Providers)
        {
            if (provider.Kind == FolderBoardStoreProvider.KindId)
                Stores.Add(new StoreChoice(provider.Kind, "In a folder", "Plain files in a folder you choose (inside a repository, they're committed with your code).", true));
            else
                Stores.Add(new StoreChoice(provider.Kind, provider.DisplayName, "Choose the repository's folder.", true));
        }
        _store = Stores[0];
        if (suggested is not null)
        {
            _store = Stores.FirstOrDefault(s => s.Kind == suggested.StoreKind) ?? Stores[0];
            _folder = suggested.Location;
            _name = Path.GetFileName(suggested.Location.TrimEnd('\\', '/'));
            if (string.Equals(_name, "board", StringComparison.OrdinalIgnoreCase))
                _name = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(suggested.Location.TrimEnd('\\', '/'))) ?? "") ?? _name;
            _key = SuggestKey(_name);
        }
    }

    private readonly IDialogService? _dialogs;

    public override string Title => "New Board";
    public override string OkText => "Create";
    public override bool FitsContent => true;
    public override double Width => 560;
    public override double MinHeight => 0;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private BoardPreset _preset = BoardPreset.Scrum;
    public IReadOnlyList<BoardPreset> Presets { get; } = Enum.GetValues<BoardPreset>();

    public ObservableCollection<StoreChoice> Stores { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowFolder))] private StoreChoice _store;
    [ObservableProperty] private string _folder = "";
    public bool ShowFolder => Store.NeedsFolder;

    [ObservableProperty] private string? _error;

    public string PresetDescription => Preset switch
    {
        BoardPreset.Scrum => "To Do, In Progress, In Review, Done; sprints with Board.Scrum.",
        BoardPreset.Kanban => "Backlog, Selected for Development, In Progress, Done; a continuous flow.",
        BoardPreset.BugTriage => "New, Triaged, In Progress, Fixed, Won't Fix; a board of bugs.",
        _ => "No columns: add your own on the board.",
    };

    /// <summary>The board, created and opened (the caller releases it when done).</summary>
    public BoardContext? Created { get; private set; }

    partial void OnPresetChanged(BoardPreset value) => OnPropertyChanged(nameof(PresetDescription));

    partial void OnNameChanged(string value)
    {
        if (!_keyEdited)
        {
            _settingKey = true;
            Key = SuggestKey(value);
            _settingKey = false;
        }
    }

    private bool _settingKey;

    partial void OnKeyChanged(string value)
    {
        if (!_settingKey)
            _keyEdited = true;
    }

    /// <summary>"DevHub Board" → "DB", "Website" → "WEB".</summary>
    public static string SuggestKey(string name)
    {
        var words = name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsAsciiLetterOrDigit).ToArray())).Where(w => w.Length > 0).ToList();
        var key = words.Count switch
        {
            0 => "",
            1 => words[0].Length <= 4 ? words[0] : words[0][..3],
            _ => string.Concat(words.Take(4).Select(w => w[0])),
        };
        key = key.ToUpperInvariant();
        while (key.Length > 0 && !char.IsAsciiLetter(key[0]))
            key = key[1..];
        return key;
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (_dialogs is null)
            return;
        if (await _dialogs.PickFolderAsync(Store.Kind == FolderBoardStoreProvider.KindId ? "Folder for the board" : "Repository folder") is { } folder)
            Folder = folder;
    }

    protected override async Task<bool> ApplyChangesAsync()
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(Name))
        {
            Error = "Give the board a name.";
            return false;
        }
        var key = Key.Trim().ToUpperInvariant();
        if (!IssueKey.IsValidProjectKey(key))
        {
            Error = "The key is 1–10 capital letters or digits, starting with a letter (like DEV).";
            return false;
        }

        BoardLocation location;
        if (Store.Kind == "devhub")
        {
            var folder = Path.Combine(BoardLocation.DefaultBoardsFolder, key);
            for (var n = 2; FolderBoardStore.HasBoard(folder); n++)
                folder = Path.Combine(BoardLocation.DefaultBoardsFolder, key + "-" + n);
            location = new BoardLocation(FolderBoardStoreProvider.KindId, folder);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Folder))
            {
                Error = "Choose a folder.";
                return false;
            }
            var provider = _boards.Provider(Store.Kind)!;
            if (!await provider.CanUseAsync(Folder))
            {
                Error = Store.Kind == FolderBoardStoreProvider.KindId ? "That folder can't hold a board." : "That folder isn't a git repository.";
                return false;
            }
            location = new BoardLocation(Store.Kind, Path.GetFullPath(Folder));
        }

        try
        {
            Created = await _boards.CreateAsync(location, new CreateBoard(key, Name.Trim(), Preset, _boards.User));
            return true;
        }
        catch (BoardRuleException e)
        {
            Error = e.Message;
        }
        catch (BoardConflictException)
        {
            Error = "There's already a board there: use Open Board instead.";
        }
        catch (Exception e) when (e is BoardStoreException or IOException or UnauthorizedAccessException)
        {
            Error = "The board couldn't be created: " + e.Message;
        }
        return false;
    }
}

/// <summary>
/// Create Issue: type, summary, description, and the fields most often set at once. "Create another" keeps the
/// dialog open for the next one. <see cref="CreatedId"/> is the last issue created.
/// </summary>
public sealed partial class CreateIssueDialogViewModel : DialogViewModelBase
{
    private readonly BoardContext _context;
    private readonly CreateIssueDefaults _defaults;

    public CreateIssueDialogViewModel(BoardContext context, CreateIssueDefaults defaults)
    {
        _context = context;
        _defaults = defaults;
        var snapshot = context.Snapshot;
        var project = snapshot.Project;
        Types = project.IssueTypes.Select(t => new NamedItem(t.Id, t.Name, t.Icon)).ToList();
        _type = Types.FirstOrDefault(t => t.Id == defaults.Type)
                ?? Types.FirstOrDefault(t => project.IssueType(t.Id)?.Level == IssueTypeLevel.Standard && t.Id == "story")
                ?? Types.FirstOrDefault(t => project.IssueType(t.Id)?.Level == IssueTypeLevel.Standard) ?? Types[0];
        Priorities = project.Priorities.Select(p => new NamedItem(p.Id, p.Name, p.Icon)).ToList();
        _priority = Priorities.FirstOrDefault(p => p.Id == project.DefaultPriorityId) ?? Priorities.FirstOrDefault();
        People = [new NamedItem(null, "Unassigned"), .. project.People.Select(p => new NamedItem(p.Id, p.Name))];
        _assignee = People.FirstOrDefault(p => p.Id == defaults.Assignee) ?? People[0];
        Sprints = [new NamedItem(null, "Backlog"), .. snapshot.Sprints.Values.Where(s => s.State != SprintState.Closed)
            .OrderBy(s => s.Sequence).Select(s => new NamedItem(s.Id, s.Name))];
        _sprint = Sprints.FirstOrDefault(s => s.Id == defaults.Sprint) ?? Sprints[0];
        UpdateParents();
        _parent = Parents.FirstOrDefault(p => p.Id == defaults.Parent) ?? Parents.FirstOrDefault();
    }

    public override string Title => "Create Issue";
    public override string OkText => "Create";
    public override double Width => 620;
    public override double Height => 600;

    public IReadOnlyList<NamedItem> Types { get; }
    public IReadOnlyList<NamedItem> Priorities { get; }
    public IReadOnlyList<NamedItem> People { get; }
    public IReadOnlyList<NamedItem> Sprints { get; }
    public ObservableCollection<NamedItem> Parents { get; } = [];

    [ObservableProperty] private NamedItem _type;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private NamedItem? _priority;
    [ObservableProperty] private NamedItem _assignee;
    [ObservableProperty] private NamedItem? _parent;
    [ObservableProperty] private NamedItem _sprint;
    [ObservableProperty] private string _points = "";
    [ObservableProperty] private string _estimate = "";
    [ObservableProperty] private string _labels = "";
    [ObservableProperty] private bool _createAnother;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _lastCreated;

    public bool HasSprints => Sprints.Count > 1;
    public bool ShowSprint => HasSprints && Level == IssueTypeLevel.Standard;
    public bool ShowParent => Level != IssueTypeLevel.Epic;
    public string ParentLabel => Level == IssueTypeLevel.Subtask ? "Parent" : "Epic";
    public bool ShowPoints => Level != IssueTypeLevel.Subtask;

    /// <summary>Asks the summary box for focus (after "Create another").</summary>
    [ObservableProperty] private int _focusSummary;

    public string? CreatedId { get; private set; }

    private IssueTypeLevel Level => _context.Project.IssueType(Type.Id)?.Level ?? IssueTypeLevel.Standard;

    partial void OnTypeChanged(NamedItem value)
    {
        var parent = Parent?.Id;
        UpdateParents();
        Parent = Parents.FirstOrDefault(p => p.Id == parent) ?? Parents.FirstOrDefault();
        OnPropertyChanged(nameof(ShowSprint));
        OnPropertyChanged(nameof(ShowParent));
        OnPropertyChanged(nameof(ParentLabel));
        OnPropertyChanged(nameof(ShowPoints));
    }

    private void UpdateParents()
    {
        var snapshot = _context.Snapshot;
        Parents.Clear();
        var level = Level;
        if (level == IssueTypeLevel.Epic)
            return;
        var wanted = level == IssueTypeLevel.Subtask ? IssueTypeLevel.Standard : IssueTypeLevel.Epic;
        if (level == IssueTypeLevel.Standard)
            Parents.Add(new NamedItem(null, "No epic"));
        foreach (var issue in snapshot.Issues.Values.Where(i => snapshot.LevelOf(i) == wanted && (!snapshot.IsDone(i) || i.Id == _defaults.Parent))
                     .OrderBy(i => i.Rank, StringComparer.Ordinal))
            Parents.Add(new NamedItem(issue.Id, $"{issue.Key} {issue.Summary}"));
    }

    protected override async Task<bool> ApplyChangesAsync()
    {
        Error = null;
        var fields = new List<(string, FieldValue)>();
        if (Priority?.Id is { } priority)
            fields.Add((IssueFields.Priority, new FieldValue.Text(priority)));
        if (Assignee.Id is { } assignee)
            fields.Add((IssueFields.Assignee, new FieldValue.Text(assignee)));
        if (ShowSprint && Sprint.Id is { } sprint)
            fields.Add((IssueFields.Sprint, new FieldValue.Text(sprint)));
        if (Points.Trim().Length > 0)
        {
            if (!double.TryParse(Points.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var points))
            {
                Error = "Story points must be a number.";
                return false;
            }
            fields.Add((IssueFields.StoryPoints, new FieldValue.Number(points)));
        }
        if (Estimate.Trim().Length > 0)
        {
            if (!WorkDuration.TryParse(Estimate, _context.Project.Settings, out var minutes))
            {
                Error = "An estimate looks like 2h, 1d 4h or 30m.";
                return false;
            }
            fields.Add((IssueFields.OriginalEstimate, new FieldValue.Minutes(minutes)));
        }
        if (Labels.Trim().Length > 0)
            fields.Add((IssueFields.Labels, new FieldValue.Items(Labels.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))));

        var id = BoardIds.New();
        var operation = new CreateIssue(Type.Id!, Summary.Trim())
        {
            Id = id,
            Parent = ShowParent ? Parent?.Id : null,
            Description = Description,
            Status = _defaults.Status is { } s && _context.Snapshot.WorkflowFor(Type.Id)?.Statuses.Contains(s) == true ? s : null,
            Fields = fields,
            RankAbove = _defaults.RankAbove,
        };
        // Check first, so a refusal (empty summary, sub-task without parent) stays in the dialog.
        try
        {
            operation.Apply(_context.Snapshot, new OperationContext(_context.Me, DateTimeOffset.Now));
        }
        catch (BoardRuleException e)
        {
            Error = e.Message;
            return false;
        }
        if (!await _context.RunAsync(operation))
        {
            Error = "The issue couldn't be created (see the status bar).";
            return false;
        }
        CreatedId = id;
        if (!CreateAnother)
            return true;
        LastCreated = $"Created {_context.Snapshot.Issue(id)?.Key}.";
        Summary = "";
        Description = "";
        Points = "";
        Estimate = "";
        FocusSummary++;
        return false;   // stay open for the next one
    }
}

/// <summary>Log Work: time spent, when, a note, and what happens to the remaining estimate.</summary>
public sealed partial class LogWorkDialogViewModel(BoardContext context, Issue issue) : DialogViewModelBase
{
    public override string Title => $"Log Work on {issue.Key}";
    public override string OkText => "Log";
    public override bool FitsContent => true;
    public override double Width => 480;
    public override double MinHeight => 0;

    [ObservableProperty] private string _spent = "";
    [ObservableProperty] private DateTime? _day = DateTime.Today;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private bool _adjustAutomatically = true;
    [ObservableProperty] private bool _leaveRemaining;
    [ObservableProperty] private bool _setRemaining;
    [ObservableProperty] private string _newRemaining = "";
    [ObservableProperty] private string? _error;

    public string RemainingNow => issue.Time.RemainingMinutes is { } r ? context.Duration(r) : "not estimated";

    protected override async Task<bool> ApplyChangesAsync()
    {
        var settings = context.Project.Settings;
        if (!WorkDuration.TryParse(Spent, settings, out var minutes))
        {
            Error = "Time spent looks like 2h, 1d 4h or 30m.";
            return false;
        }
        int? remaining = null;
        if (SetRemaining)
        {
            if (NewRemaining.Trim() is "0" or "0m" or "0h")
                remaining = 0;
            else if (WorkDuration.TryParse(NewRemaining, settings, out var r))
                remaining = r;
            else
            {
                Error = "The remaining estimate looks like 2h, 1d 4h or 0.";
                return false;
            }
        }
        var started = Day is { } d && d.Date != DateTime.Today ? new DateTimeOffset(d.Date.AddHours(12)) : DateTimeOffset.Now;
        return await context.RunAsync(new LogWork(issue.Id, minutes, started)
        {
            Note = Note,
            Remaining = SetRemaining ? RemainingAdjustment.Set : LeaveRemaining ? RemainingAdjustment.Leave : RemainingAdjustment.Auto,
            NewRemainingMinutes = remaining,
        });
    }
}

/// <summary>Bulk edit: one field, one value, for every selected issue.</summary>
public sealed partial class BulkEditDialogViewModel : DialogViewModelBase
{
    private readonly BoardContext _context;
    private readonly IReadOnlyList<string> _issueIds;

    public BulkEditDialogViewModel(BoardContext context, IReadOnlyList<string> issueIds)
    {
        _context = context;
        _issueIds = issueIds;
        var project = context.Project;
        Fields =
        [
            new NamedItem(IssueFields.Assignee, "Assignee"), new NamedItem(IssueFields.Priority, "Priority"),
            new NamedItem(IssueFields.StoryPoints, "Story points"), new NamedItem(IssueFields.Labels, "Labels"),
            new NamedItem(IssueFields.Parent, "Epic"), new NamedItem(IssueFields.FixVersions, "Fix versions"),
            new NamedItem(IssueFields.Components, "Components"), new NamedItem(IssueFields.Due, "Due date"),
            .. (context.Snapshot.Sprints.Count > 0 ? [new NamedItem(IssueFields.Sprint, "Sprint")] : Array.Empty<NamedItem>()),
            new NamedItem("status", "Status (transition)"),
        ];
        _field = Fields[0];
        UpdateChoices();
    }

    public override string Title => $"Edit {_issueIds.Count} Issues";
    public override string OkText => "Apply";
    public override bool FitsContent => true;
    public override double Width => 480;
    public override double MinHeight => 0;

    public IReadOnlyList<NamedItem> Fields { get; }
    [ObservableProperty] private NamedItem _field;
    public ObservableCollection<NamedItem> Choices { get; } = [];
    [ObservableProperty] private NamedItem? _choice;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _showChoices;
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private string? _error;

    partial void OnFieldChanged(NamedItem value) => UpdateChoices();

    private void UpdateChoices()
    {
        var snapshot = _context.Snapshot;
        var project = snapshot.Project;
        Choices.Clear();
        IEnumerable<NamedItem> items = Field.Id switch
        {
            IssueFields.Assignee => [new NamedItem(null, "Unassigned"), .. project.People.Select(p => new NamedItem(p.Id, p.Name))],
            IssueFields.Priority => project.Priorities.Select(p => new NamedItem(p.Id, p.Name)),
            IssueFields.Parent => [new NamedItem(null, "No epic"), .. snapshot.Issues.Values.Where(i => snapshot.LevelOf(i) == IssueTypeLevel.Epic)
                .OrderBy(i => i.Rank, StringComparer.Ordinal).Select(i => new NamedItem(i.Id, $"{i.Key} {i.Summary}"))],
            IssueFields.Sprint => [new NamedItem(null, "Backlog"), .. snapshot.Sprints.Values.Where(s => s.State != SprintState.Closed)
                .OrderBy(s => s.Sequence).Select(s => new NamedItem(s.Id, s.Name))],
            "status" => project.Statuses.Select(s => new NamedItem(s.Id, s.Name)),
            _ => [],
        };
        foreach (var item in items)
            Choices.Add(item);
        ShowChoices = Choices.Count > 0;
        Choice = Choices.FirstOrDefault();
        Hint = Field.Id switch
        {
            IssueFields.StoryPoints => "A number; empty clears it.",
            IssueFields.Labels => "Labels separated by spaces; empty clears them.",
            IssueFields.FixVersions => "Version names separated by commas; empty clears them.",
            IssueFields.Components => "Component names separated by commas; empty clears them.",
            IssueFields.Due => "A date like 2026-10-31; empty clears it.",
            _ => "",
        };
    }

    protected override async Task<bool> ApplyChangesAsync()
    {
        Error = null;
        var project = _context.Project;
        if (Field.Id == "status")
        {
            var refused = 0;
            foreach (var id in _issueIds)
            {
                var issue = _context.Snapshot.Issue(id);
                if (issue is null || issue.Status == Choice?.Id)
                    continue;
                if (!await _context.RunAsync(new TransitionIssue(id, Choice!.Id!)))
                    refused++;
            }
            if (refused > 0)
                _context.Status.Show($"{refused} issue(s) couldn't move there: their workflow doesn't allow it.");
            return true;
        }

        FieldValue value;
        switch (Field.Id)
        {
            case IssueFields.StoryPoints:
                if (Text.Trim().Length == 0)
                    value = new FieldValue.Number(null);
                else if (double.TryParse(Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    value = new FieldValue.Number(n);
                else
                {
                    Error = "Story points must be a number.";
                    return false;
                }
                break;
            case IssueFields.Labels:
                value = new FieldValue.Items(Text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries));
                break;
            case IssueFields.FixVersions:
            case IssueFields.Components:
            {
                var names = Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var ids = new List<string>();
                foreach (var name in names)
                {
                    var id = Field.Id == IssueFields.FixVersions
                        ? _context.Snapshot.Versions.Values.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))?.Id
                        : project.Components.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;
                    if (id is null)
                    {
                        Error = $"There's no {(Field.Id == IssueFields.FixVersions ? "version" : "component")} \"{name}\".";
                        return false;
                    }
                    ids.Add(id);
                }
                value = new FieldValue.Items(ids);
                break;
            }
            case IssueFields.Due:
                if (Text.Trim().Length == 0)
                    value = new FieldValue.Date(null);
                else if (DateOnly.TryParse(Text.Trim(), CultureInfo.CurrentCulture, out var date)
                         || DateOnly.TryParseExact(Text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    value = new FieldValue.Date(date);
                else
                {
                    Error = "A date looks like 2026-10-31.";
                    return false;
                }
                break;
            default:
                value = new FieldValue.Text(Choice?.Id);
                break;
        }

        // Only the issues the field applies to (an epic has no epic, a sub-task no sprint of its own).
        var snapshot = _context.Snapshot;
        var ids2 = _issueIds.Where(id => snapshot.Issue(id) is { } i && Field.Id switch
        {
            IssueFields.Parent => snapshot.LevelOf(i) == IssueTypeLevel.Standard,
            IssueFields.Sprint => snapshot.LevelOf(i) != IssueTypeLevel.Subtask,
            _ => true,
        }).ToList();
        if (ids2.Count == 0)
        {
            Error = "That field doesn't apply to any of the selected issues.";
            return false;
        }
        return await _context.RunAsync(new BulkEdit(ids2, [(Field.Id!, value)]));
    }
}
