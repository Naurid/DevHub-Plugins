using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using DevHub.Controls;
using DevHub.Services.Board;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Documents;

/// <summary>
/// Project settings as a tab ("BoardSettings:folder|C:\…"): general, the workflow editor, statuses, issue types,
/// priorities, resolutions, custom fields, components, versions, people and link types. Every change is saved at
/// once (Undo in the board's menu takes it back).
/// </summary>
public sealed partial class ProjectSettingsDocumentViewModel : DocumentViewModelBase, IDisposable
{
    public const string IdPrefix = "BoardSettings:";

    private readonly BoardUiServices _services;
    private BoardContext? _context;
    private bool _loading;
    private bool _disposed;
    private string _title = "Board settings";

    public ProjectSettingsDocumentViewModel(BoardLocation location, BoardUiServices services)
    {
        Location = location;
        _services = services;
        Sections = ["General", "Workflows", "Statuses", "Issue types", "Priorities", "Resolutions", "Definition of Done", "Custom fields",
            "Components", "Versions", "People", "Link types"];
        _section = Sections[0];
    }

    public BoardLocation Location { get; }
    public override string Id => ProjectSettingsDocumentFactory.IdFor(Location);
    public override string Title => _title;
    public override string Icon => IconKind.Settings;
    public override string? ContextPath => Location.Location;

    public IReadOnlyList<string> Sections { get; }
    [ObservableProperty] private string _section;
    [ObservableProperty] private ProjectSettingsModel? _model;
    [ObservableProperty] private string? _message;

    public bool IsGeneral => Section == "General";
    public bool IsWorkflows => Section == "Workflows";
    public bool IsStatuses => Section == "Statuses";
    public bool IsIssueTypes => Section == "Issue types";
    public bool IsPriorities => Section == "Priorities";
    public bool IsResolutions => Section == "Resolutions";
    public bool IsDefinitionOfDone => Section == "Definition of Done";
    public bool IsCustomFields => Section == "Custom fields";
    public bool IsComponents => Section == "Components";
    public bool IsVersions => Section == "Versions";
    public bool IsPeople => Section == "People";
    public bool IsLinkTypes => Section == "Link types";

    partial void OnSectionChanged(string value)
    {
        foreach (var name in new[] { nameof(IsGeneral), nameof(IsWorkflows), nameof(IsStatuses), nameof(IsIssueTypes), nameof(IsPriorities),
                     nameof(IsResolutions), nameof(IsDefinitionOfDone), nameof(IsCustomFields), nameof(IsComponents), nameof(IsVersions), nameof(IsPeople), nameof(IsLinkTypes) })
            OnPropertyChanged(name);
    }

    protected override void OnActivationChanged(bool isActive)
    {
        if (isActive && _context is null && !_loading)
            _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _context = await _services.Boards.AcquireAsync(Location);
            if (_disposed)
            {
                if (_context is not null)
                    _services.Boards.Release(_context);
                return;
            }
            if (_context is null)
            {
                Message = "The board isn't there any more.";
                return;
            }
            _context.Changed += OnBoardChanged;
            Model = new ProjectSettingsModel(_context);
            UpdateTitle();
        }
        catch (Exception e) when (e is BoardStoreException or IOException or UnauthorizedAccessException)
        {
            Message = "The board can't be opened: " + e.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnBoardChanged(object? sender, EventArgs e)
    {
        Model?.Reload();
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var title = $"{_context?.Project.Key} settings";
        if (title == _title)
            return;
        _title = title;
        OnPropertyChanged(nameof(Title));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_context is not null)
        {
            _context.Changed -= OnBoardChanged;
            _services.Boards.Release(_context);
            _context = null;
        }
    }
}

public sealed class ProjectSettingsDocumentFactory(BoardUiServices services) : IDocumentFactory
{
    public string IdPrefix => ProjectSettingsDocumentViewModel.IdPrefix;

    public static string IdFor(BoardLocation location) => ProjectSettingsDocumentViewModel.IdPrefix + location.Key;

    public DocumentViewModelBase? Create(string id)
    {
        if (!id.StartsWith(IdPrefix, StringComparison.Ordinal) || BoardLocation.FromKey(id[IdPrefix.Length..]) is not { } location
            || services.Boards.Provider(location.StoreKind) is null)
            return null;
        return new ProjectSettingsDocumentViewModel(location, services);
    }
}

/// <summary>A row with a name you edit in place (saved when the box loses focus) and a delete button.</summary>
public sealed partial class NamedRow : ObservableObject
{
    private readonly Func<NamedRow, string, Task>? _rename;
    private bool _loading = true;

    public NamedRow(string id, string name, Func<NamedRow, string, Task>? rename, string? detail = null)
    {
        Id = id;
        _name = name;
        _rename = rename;
        Detail = detail;
        _loading = false;
    }

    public string Id { get; }
    [ObservableProperty] private string _name;
    public string? Detail { get; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>Extra values some sections use (icon, category, email, options…).</summary>
    [ObservableProperty] private string _second = "";
    [ObservableProperty] private string _third = "";
    [ObservableProperty] private bool _flag;
    [ObservableProperty] private bool _flag2;
    [ObservableProperty] private NamedItem? _choice;
    [ObservableProperty] private NamedItem? _choice2;
    public IReadOnlyList<NamedItem> Choices { get; init; } = [];
    public IReadOnlyList<NamedItem> Choices2 { get; init; } = [];

    /// <summary>Called when one of the extra values changes (after loading).</summary>
    public Func<NamedRow, string, Task>? Changed { get; init; }

    internal void Loaded() => _loading = false;
    internal void Loading() => _loading = true;

    partial void OnNameChanged(string value)
    {
        if (!_loading && _rename is not null)
            _ = _rename(this, value);
    }

    partial void OnSecondChanged(string value) => Notify(nameof(Second));
    partial void OnThirdChanged(string value) => Notify(nameof(Third));
    partial void OnFlagChanged(bool value) => Notify(nameof(Flag));
    partial void OnFlag2Changed(bool value) => Notify(nameof(Flag2));
    partial void OnChoiceChanged(NamedItem? value) => Notify(nameof(Choice));
    partial void OnChoice2Changed(NamedItem? value) => Notify(nameof(Choice2));

    private void Notify(string what)
    {
        if (!_loading && Changed is not null)
            _ = Changed(this, what);
    }

    public override string ToString() => Name;
}

/// <summary>A cell of the transition grid: may an issue move from <see cref="From"/> to <see cref="To"/>?</summary>
public sealed partial class TransitionCell(string? from, string to, bool allowed, bool isSelf, Action changed) : ObservableObject
{
    public string? From { get; } = from;
    public string To { get; } = to;
    public bool IsSelf { get; } = isSelf;
    public bool IsEnabled => !IsSelf;
    [ObservableProperty] private bool _isAllowed = allowed;
    partial void OnIsAllowedChanged(bool value) => changed();
}

/// <summary>A row of the transition grid: one "from" status (or "any status").</summary>
public sealed record TransitionRow(string FromName, IReadOnlyList<TransitionCell> Cells);

/// <summary>Everything the settings tab shows, rebuilt from the board after each change.</summary>
public sealed partial class ProjectSettingsModel : ObservableObject
{
    private readonly BoardContext _context;
    private bool _reloading;

    private static readonly IReadOnlyList<NamedItem> TypeIcons =
        new[] { "epic", "story", "task", "bug", "subtask", "spike" }.Select(i => new NamedItem(i, char.ToUpperInvariant(i[0]) + i[1..], i)).ToList();
    private static readonly IReadOnlyList<NamedItem> PriorityIcons =
        new[] { "highest", "high", "medium", "low", "lowest" }.Select(i => new NamedItem(i, char.ToUpperInvariant(i[0]) + i[1..], i)).ToList();
    private static readonly IReadOnlyList<NamedItem> Categories =
        [new("ToDo", "To Do"), new("InProgress", "In Progress"), new("Done", "Done")];

    public ProjectSettingsModel(BoardContext context)
    {
        _context = context;
        Reload();
    }

    public BoardContext Context => _context;
    private BoardSnapshot Snapshot => _context.Snapshot;
    private Task<bool> Run(IBoardOperation operation) => _context.RunAsync(operation);

    // ---- General ------------------------------------------------------------------------------------------

    [ObservableProperty] private string _projectName = "";
    [ObservableProperty] private string _projectKey = "";
    [ObservableProperty] private string _hoursPerDay = "";
    [ObservableProperty] private string _daysPerWeek = "";
    public string LocationText => $"{_context.Session.Store.DisplayLocation}";

    partial void OnProjectNameChanged(string value)
    {
        if (!_reloading && value.Trim() != Snapshot.Project.Name)
            _ = Run(new RenameProject(value));
    }

    partial void OnHoursPerDayChanged(string value) => SaveTimeTracking();
    partial void OnDaysPerWeekChanged(string value) => SaveTimeTracking();

    private void SaveTimeTracking()
    {
        if (_reloading)
            return;
        if (double.TryParse(HoursPerDay, NumberStyles.Float, CultureInfo.CurrentCulture, out var hours)
            && double.TryParse(DaysPerWeek, NumberStyles.Float, CultureInfo.CurrentCulture, out var days))
            _ = Run(new SetTimeTracking(hours, days));
        else
            _context.Status.Show("Hours per day and days per week are numbers.");
    }

    [RelayCommand]
    private async Task ChangeKeyAsync()
    {
        var dialog = new TextInputDialogViewModel("Change Project Key", "New key", Snapshot.Project.Key, "Change")
        {
            Explanation = $"Every issue gets the new key ({Snapshot.Project.Key}-12 becomes NEW-12). The old keys keep working in branch names and commit messages.",
        };
        if (await _context.Dialogs.ShowDialogAsync(dialog))
            await Run(new ChangeProjectKey(dialog.Text));
    }

    // ---- Workflows ----------------------------------------------------------------------------------------

    public ObservableCollection<NamedRow> Workflows { get; } = [];
    [ObservableProperty] private NamedRow? _selectedWorkflow;
    public ObservableCollection<NamedRow> WorkflowStatuses { get; } = [];
    public ObservableCollection<NamedItem> StatusesToAdd { get; } = [];
    [ObservableProperty] private NamedItem? _statusToAdd;
    [ObservableProperty] private bool _anyToAny;
    [ObservableProperty] private bool _isDefaultWorkflow;
    [ObservableProperty] private string _workflowUsers = "";
    public ObservableCollection<string> TransitionColumns { get; } = [];
    public ObservableCollection<TransitionRow> TransitionRows { get; } = [];

    partial void OnSelectedWorkflowChanged(NamedRow? value)
    {
        if (!_reloading)
            LoadWorkflow();
    }

    partial void OnAnyToAnyChanged(bool value)
    {
        if (_reloading || SelectedWorkflow is not { } wf || !Snapshot.Workflows.TryGetValue(wf.Id, out var workflow))
            return;
        // Leaving "any to any" with no transitions yet would block every move: start with every move allowed
        // (one "from any status" transition per status), so the grid only needs unticking.
        if (!value && workflow.Transitions.Count == 0)
            _ = Run(new SetTransitions(wf.Id, false, workflow.Statuses.Select(s => new WorkflowTransition { To = s }).ToList()));
        else
            _ = SaveTransitions(wf.Id);
    }

    private void LoadWorkflow()
    {
        var wasReloading = _reloading;
        _reloading = true;
        try
        {
            WorkflowStatuses.Clear();
            StatusesToAdd.Clear();
            TransitionColumns.Clear();
            TransitionRows.Clear();
            if (SelectedWorkflow is null || !Snapshot.Workflows.TryGetValue(SelectedWorkflow.Id, out var workflow))
                return;
            var project = Snapshot.Project;
            for (var i = 0; i < workflow.Statuses.Count; i++)
            {
                var id = workflow.Statuses[i];
                WorkflowStatuses.Add(new NamedRow(id, _context.StatusName(id), null,
                    (i == 0 ? "first: new issues start here · " : "") + CategoryName(project.Status(id)?.Category)));
            }
            foreach (var s in project.Statuses.Where(s => !workflow.Statuses.Contains(s.Id)))
                StatusesToAdd.Add(new NamedItem(s.Id, s.Name));
            StatusToAdd = StatusesToAdd.FirstOrDefault();
            AnyToAny = workflow.AnyToAny;
            IsDefaultWorkflow = Snapshot.DefaultWorkflow?.Id == workflow.Id;
            var users = project.IssueTypes.Where(t => Snapshot.WorkflowFor(t.Id)?.Id == workflow.Id).Select(t => t.Name).ToList();
            WorkflowUsers = users.Count == 0 ? "No issue type uses it." : "Used by: " + string.Join(", ", users);

            foreach (var to in workflow.Statuses)
                TransitionColumns.Add(_context.StatusName(to));
            void Changed() => _ = SaveTransitions(workflow.Id);
            TransitionRows.Add(new TransitionRow("From any status", workflow.Statuses
                .Select(to => new TransitionCell(null, to, workflow.Transitions.Any(t => t.From is null && t.To == to), false, Changed)).ToList()));
            foreach (var from in workflow.Statuses)
            {
                TransitionRows.Add(new TransitionRow(_context.StatusName(from), workflow.Statuses
                    .Select(to => new TransitionCell(from, to, workflow.Transitions.Any(t => t.From == from && t.To == to), from == to, Changed)).ToList()));
            }
        }
        finally
        {
            _reloading = wasReloading;
        }
    }

    private async Task SaveTransitions(string workflowId)
    {
        if (_reloading)
            return;
        var transitions = TransitionRows.SelectMany(r => r.Cells).Where(c => c.IsAllowed && !c.IsSelf)
            .Select(c => new WorkflowTransition { From = c.From, To = c.To }).ToList();
        await Run(new SetTransitions(workflowId, AnyToAny, transitions));
    }

    [RelayCommand]
    private async Task NewWorkflowAsync()
    {
        var dialog = new TextInputDialogViewModel("New Workflow", "Name", "", "Create")
        {
            Choice = new ChoiceInput("Start from", ["A copy of " + (SelectedWorkflow?.Name ?? "the default"), "Just the first status"], "A copy of " + (SelectedWorkflow?.Name ?? "the default")),
        };
        if (!await _context.Dialogs.ShowDialogAsync(dialog))
            return;
        var id = BoardIds.New();
        var copy = dialog.Choice!.Selected.StartsWith("A copy", StringComparison.Ordinal) ? SelectedWorkflow?.Id ?? Snapshot.DefaultWorkflow?.Id : null;
        if (await Run(new CreateWorkflow(dialog.Text) { Id = id, CopyOf = copy }))
            SelectedWorkflow = Workflows.FirstOrDefault(w => w.Id == id);
    }

    [RelayCommand]
    private async Task DeleteWorkflowAsync()
    {
        if (SelectedWorkflow is not { } wf)
            return;
        if (await _context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Delete Workflow", $"Delete the workflow {wf.Name}?", "Delete", [wf.Name])))
            await Run(new DeleteWorkflow(wf.Id));
    }

    [RelayCommand]
    private async Task MakeDefaultWorkflowAsync()
    {
        if (SelectedWorkflow is { } wf)
            await Run(new SetDefaultWorkflow(wf.Id));
    }

    [RelayCommand]
    private async Task AddStatusToWorkflowAsync()
    {
        if (SelectedWorkflow is { } wf && StatusToAdd?.Id is { } status)
            await Run(new AddStatusToWorkflow(wf.Id, status));
    }

    [RelayCommand]
    private async Task RemoveStatusFromWorkflowAsync(NamedRow? row)
    {
        if (SelectedWorkflow is not { } wf || row is null || !Snapshot.Workflows.TryGetValue(wf.Id, out var workflow))
            return;
        var others = workflow.Statuses.Where(s => s != row.Id).Select(_context.StatusName).ToList();
        if (others.Count == 0)
            return;
        var dialog = new TextInputDialogViewModel($"Remove {row.Name} from {wf.Name}", "", "", "Remove")
        {
            ShowText = false,
            Explanation = "Issues of the types that follow this workflow and are in this status move to:",
            Choice = new ChoiceInput("Move them to", others, others[0]),
        };
        if (!await _context.Dialogs.ShowDialogAsync(dialog))
            return;
        var target = workflow.Statuses.First(s => _context.StatusName(s) == dialog.Choice!.Selected);
        await Run(new RemoveStatusFromWorkflow(wf.Id, row.Id, target));
    }

    [RelayCommand]
    private async Task MoveWorkflowStatusUpAsync(NamedRow? row) => await MoveWorkflowStatus(row, -1);

    [RelayCommand]
    private async Task MoveWorkflowStatusDownAsync(NamedRow? row) => await MoveWorkflowStatus(row, 1);

    private async Task MoveWorkflowStatus(NamedRow? row, int step)
    {
        if (SelectedWorkflow is not { } wf || row is null)
            return;
        var index = WorkflowStatuses.IndexOf(row) + step;
        if (index >= 0 && index < WorkflowStatuses.Count)
            await Run(new MoveStatusInWorkflow(wf.Id, row.Id, index));
    }

    // ---- Statuses -----------------------------------------------------------------------------------------

    public ObservableCollection<NamedRow> Statuses { get; } = [];

    [RelayCommand]
    private async Task AddStatusAsync()
    {
        var dialog = new TextInputDialogViewModel("Add Status", "Name", "", "Add")
        {
            Explanation = "The status joins every workflow (at the end). Put it on a board with a column's Statuses… menu.",
            Choice = new ChoiceInput("Counts as", Categories.Select(c => c.Name).ToList(), "In Progress"),
        };
        if (await _context.Dialogs.ShowDialogAsync(dialog))
            await Run(new AddStatus(dialog.Text, ParseCategory(Categories.First(c => c.Name == dialog.Choice!.Selected).Id)));
    }

    [RelayCommand]
    private async Task DeleteStatusAsync(NamedRow? row)
    {
        if (row is null)
            return;
        var count = Snapshot.Issues.Values.Count(i => i.Status == row.Id);
        var others = Snapshot.Project.Statuses.Where(s => s.Id != row.Id).ToList();
        var dialog = new TextInputDialogViewModel($"Delete {row.Name}", "", "", "Delete")
        {
            ShowText = false,
            Destructive = true,
            Explanation = count == 0
                ? $"Delete the status {row.Name}? It leaves every workflow and board column."
                : $"{count} issue(s) are in {row.Name}. It leaves every workflow and board column; its issues move to:",
            Choice = count == 0 || others.Count == 0 ? null : new ChoiceInput("Move them to", others.Select(o => o.Name).ToList(), others[0].Name),
        };
        if (!await _context.Dialogs.ShowDialogAsync(dialog))
            return;
        var target = others.FirstOrDefault(o => o.Name == dialog.Choice?.Selected)?.Id;
        await Run(new DeleteStatus(row.Id, target));
    }

    // ---- Issue types ----------------------------------------------------------------------------------------

    public ObservableCollection<NamedRow> IssueTypes { get; } = [];

    [RelayCommand]
    private async Task AddIssueTypeAsync()
    {
        var dialog = new TextInputDialogViewModel("Add Issue Type", "Name", "", "Add")
        {
            Choice = new ChoiceInput("Level", ["Standard (like Story, Task)", "Epic (holds standard issues)", "Sub-task (inside a standard issue)"], "Standard (like Story, Task)"),
            SecondChoice = new ChoiceInput("Icon", TypeIcons.Select(i => i.Name).ToList(), "Task"),
        };
        if (!await _context.Dialogs.ShowDialogAsync(dialog))
            return;
        var level = dialog.Choice!.Selected.StartsWith("Epic", StringComparison.Ordinal) ? IssueTypeLevel.Epic
            : dialog.Choice.Selected.StartsWith("Sub", StringComparison.Ordinal) ? IssueTypeLevel.Subtask : IssueTypeLevel.Standard;
        var icon = TypeIcons.First(i => i.Name == dialog.SecondChoice!.Selected).Id!;
        await Run(new AddIssueType(dialog.Text, level, icon));
    }

    [RelayCommand]
    private async Task DeleteIssueTypeAsync(NamedRow? row)
    {
        if (row is null || Snapshot.Project.IssueType(row.Id) is not { } type)
            return;
        var count = Snapshot.Issues.Values.Count(i => i.Type == row.Id);
        var others = Snapshot.Project.IssueTypes.Where(t => t.Id != row.Id && t.Level == type.Level).ToList();
        if (count > 0 && others.Count == 0)
        {
            _context.Status.Show($"{count} issue(s) are {type.Name}s and there's no other type of that level to turn them into.");
            return;
        }
        var dialog = new TextInputDialogViewModel($"Delete {type.Name}", "", "", "Delete")
        {
            ShowText = false,
            Destructive = true,
            Explanation = count == 0 ? $"Delete the issue type {type.Name}?" : $"{count} issue(s) are {type.Name}s. They become:",
            Choice = count == 0 ? null : new ChoiceInput("Turn them into", others.Select(o => o.Name).ToList(), others[0].Name),
        };
        if (!await _context.Dialogs.ShowDialogAsync(dialog))
            return;
        await Run(new DeleteIssueType(row.Id, others.FirstOrDefault(o => o.Name == dialog.Choice?.Selected)?.Id));
    }

    // ---- Priorities, resolutions --------------------------------------------------------------------------

    public ObservableCollection<NamedRow> Priorities { get; } = [];
    public ObservableCollection<NamedRow> Resolutions { get; } = [];

    [RelayCommand]
    private async Task AddPriorityAsync()
    {
        var dialog = new TextInputDialogViewModel("Add Priority", "Name", "", "Add")
        {
            Choice = new ChoiceInput("Icon", PriorityIcons.Select(i => i.Name).ToList(), "Medium"),
        };
        if (await _context.Dialogs.ShowDialogAsync(dialog))
            await Run(new AddPriority(dialog.Text, PriorityIcons.First(i => i.Name == dialog.Choice!.Selected).Id!));
    }

    [RelayCommand]
    private async Task MovePriorityUpAsync(NamedRow? row)
    {
        if (row is not null && Priorities.IndexOf(row) > 0)
            await Run(new MovePriority(row.Id, Priorities.IndexOf(row) - 1));
    }

    [RelayCommand]
    private async Task MovePriorityDownAsync(NamedRow? row)
    {
        if (row is not null && Priorities.IndexOf(row) < Priorities.Count - 1)
            await Run(new MovePriority(row.Id, Priorities.IndexOf(row) + 1));
    }

    [RelayCommand]
    private async Task DeletePriorityAsync(NamedRow? row)
    {
        if (row is null)
            return;
        var others = Snapshot.Project.Priorities.Where(p => p.Id != row.Id).ToList();
        var dialog = new TextInputDialogViewModel($"Delete {row.Name}", "", "", "Delete")
        {
            ShowText = false,
            Destructive = true,
            Explanation = "Issues with this priority get:",
            Choice = new ChoiceInput("Instead", ["No priority", .. others.Select(o => o.Name)], others.FirstOrDefault()?.Name ?? "No priority"),
        };
        if (await _context.Dialogs.ShowDialogAsync(dialog))
            await Run(new DeletePriority(row.Id, others.FirstOrDefault(o => o.Name == dialog.Choice!.Selected)?.Id));
    }

    [RelayCommand]
    private async Task AddResolutionAsync()
    {
        var dialog = new TextInputDialogViewModel("Add Resolution", "Name", "", "Add");
        if (await _context.Dialogs.ShowDialogAsync(dialog))
            await Run(new AddResolution(dialog.Text));
    }

    [RelayCommand]
    private async Task DeleteResolutionAsync(NamedRow? row)
    {
        if (row is null)
            return;
        var others = Snapshot.Project.Resolutions.Where(r => r.Id != row.Id).ToList();
        if (others.Count == 0)
        {
            _context.Status.Show("Done issues need at least one resolution.");
            return;
        }
        var dialog = new TextInputDialogViewModel($"Delete {row.Name}", "", "", "Delete")
        {
            ShowText = false,
            Destructive = true,
            Explanation = "Issues resolved this way get:",
            Choice = new ChoiceInput("Instead", others.Select(o => o.Name).ToList(), others[0].Name),
        };
        if (await _context.Dialogs.ShowDialogAsync(dialog))
            await Run(new DeleteResolution(row.Id, others.First(o => o.Name == dialog.Choice!.Selected).Id));
    }

    // ---- Custom fields, components, versions, people, link types --------------------------------------------

    public ObservableCollection<NamedRow> CustomFields { get; } = [];
    public ObservableCollection<NamedRow> Components { get; } = [];
    public ObservableCollection<NamedRow> Versions { get; } = [];
    public ObservableCollection<NamedRow> People { get; } = [];
    public ObservableCollection<NamedRow> LinkTypes { get; } = [];

    [RelayCommand]
    private async Task AddCustomFieldAsync()
    {
        var dialog = new TextInputDialogViewModel("Add Field", "Name", "", "Add")
        {
            Choice = new ChoiceInput("Kind", ["Text", "Number", "Date", "Choice", "Person"], "Text"),
            SecondLabel = "Choices (for a Choice field), separated by commas",
        };
        if (!await _context.Dialogs.ShowDialogAsync(dialog))
            return;
        var type = dialog.Choice!.Selected switch
        {
            "Number" => FieldType.Number, "Date" => FieldType.Date, "Choice" => FieldType.Select, "Person" => FieldType.Person, _ => FieldType.Text,
        };
        await Run(new AddCustomField(dialog.Text, type) { Options = dialog.SecondText.Split(',') });
    }

    [RelayCommand]
    private async Task DeleteCustomFieldAsync(NamedRow? row)
    {
        if (row is null)
            return;
        var used = Snapshot.Issues.Values.Count(i => i.Custom.ContainsKey(row.Id));
        if (await _context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Delete Field",
                used == 0 ? $"Delete the field {row.Name}?" : $"Delete the field {row.Name} and its values on {used} issue(s)?", "Delete", [row.Name])))
            await Run(new DeleteCustomField(row.Id));
    }

    [RelayCommand]
    private async Task AddComponentAsync()
    {
        var dialog = new TextInputDialogViewModel("Add Component", "Name", "", "Add") { SecondLabel = "Description (optional)" };
        if (await _context.Dialogs.ShowDialogAsync(dialog))
            await Run(new AddComponent(dialog.Text, dialog.SecondText));
    }

    [RelayCommand]
    private async Task DeleteComponentAsync(NamedRow? row)
    {
        if (row is not null && await _context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Delete Component",
                $"Delete the component {row.Name}? Issues filed under it lose it.", "Delete", [row.Name])))
            await Run(new DeleteComponent(row.Id));
    }

    [RelayCommand]
    private async Task AddVersionAsync()
    {
        var dialog = new TextInputDialogViewModel("Add Version", "Name", "", "Add") { SecondLabel = "Release date (optional, like 2026-12-01)" };
        if (!await _context.Dialogs.ShowDialogAsync(dialog))
            return;
        DateOnly? release = DateOnly.TryParse(dialog.SecondText, CultureInfo.CurrentCulture, out var d)
                            || DateOnly.TryParseExact(dialog.SecondText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : null;
        await Run(new CreateVersion(dialog.Text) { Release = release });
    }

    [RelayCommand]
    private async Task DeleteVersionAsync(NamedRow? row)
    {
        if (row is not null && await _context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Delete Version",
                $"Delete the version {row.Name}? Issues planned for it lose it.", "Delete", [row.Name])))
            await Run(new DeleteVersion(row.Id));
    }

    [RelayCommand]
    private async Task AddPersonAsync()
    {
        var dialog = new TextInputDialogViewModel("Add Person", "Name", "", "Add") { SecondLabel = "Email (optional; matches git authors and avatars)" };
        if (await _context.Dialogs.ShowDialogAsync(dialog))
            await Run(new AddPerson(dialog.Text, dialog.SecondText));
    }

    [RelayCommand]
    private async Task RemovePersonAsync(NamedRow? row)
    {
        if (row is not null && await _context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Remove Person",
                $"Take {row.Name} off the board? Issues assigned to them become unassigned.", "Remove", [row.Name])))
            await Run(new RemovePerson(row.Id));
    }

    // ---- Definition of Done -------------------------------------------------------------------------------

    public ObservableCollection<NamedRow> DoneItems { get; } = [];

    public IReadOnlyList<NamedItem> DoneCheckModes { get; } =
    [
        new(nameof(DoneCheck.Off), "Off: moving to Done is never checked"),
        new(nameof(DoneCheck.Warn), "Ask: confirm when something isn't ticked"),
        new(nameof(DoneCheck.Block), "Strict: refuse until everything is ticked"),
    ];

    [ObservableProperty] private NamedItem? _doneCheckMode;

    partial void OnDoneCheckModeChanged(NamedItem? value)
    {
        if (!_reloading && value?.Id is { } id && Enum.TryParse<DoneCheck>(id, out var check))
            _ = Run(new SetDoneCheck(check));
    }

    [RelayCommand]
    private async Task AddDoneItemAsync()
    {
        var dialog = new TextInputDialogViewModel("Add to the Definition of Done", "What every story needs (\"Code reviewed\")", "", "Add");
        if (await _context.Dialogs.ShowDialogAsync(dialog))
            await Run(new AddDoneItem(dialog.Text));
    }

    [RelayCommand]
    private async Task MoveDoneItemUpAsync(NamedRow? row)
    {
        if (row is not null && DoneItems.IndexOf(row) > 0)
            await Run(new MoveDoneItem(row.Id, DoneItems.IndexOf(row) - 1));
    }

    [RelayCommand]
    private async Task MoveDoneItemDownAsync(NamedRow? row)
    {
        if (row is not null && DoneItems.IndexOf(row) < DoneItems.Count - 1)
            await Run(new MoveDoneItem(row.Id, DoneItems.IndexOf(row) + 1));
    }

    [RelayCommand]
    private async Task DeleteDoneItemAsync(NamedRow? row)
    {
        if (row is not null)
            await Run(new RemoveDoneItem(row.Id));
    }

    [RelayCommand]
    private async Task AddLinkTypeAsync()
    {
        var dialog = new TextInputDialogViewModel("Add Link Type", "From the issue holding it (\"blocks\")", "", "Add")
        {
            SecondLabel = "From the other issue (\"is blocked by\"; empty = the same)",
        };
        if (await _context.Dialogs.ShowDialogAsync(dialog))
            await Run(new AddLinkType(dialog.Text, dialog.SecondText));
    }

    [RelayCommand]
    private async Task DeleteLinkTypeAsync(NamedRow? row)
    {
        if (row is not null && await _context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Delete Link Type",
                $"Delete the link type \"{row.Name}\" and every link of that type?", "Delete", [row.Name])))
            await Run(new DeleteLinkType(row.Id));
    }

    // ---- Loading ------------------------------------------------------------------------------------------

    /// <summary>Rebuilds every list from the board (after any change).</summary>
    public void Reload()
    {
        _reloading = true;
        try
        {
            var board = Snapshot;
            var project = board.Project;
            ProjectName = project.Name;
            ProjectKey = project.Key;
            HoursPerDay = project.Settings.HoursPerDay.ToString(CultureInfo.CurrentCulture);
            DaysPerWeek = project.Settings.DaysPerWeek.ToString(CultureInfo.CurrentCulture);

            DoneItems.Clear();
            foreach (var d in project.DefinitionOfDone)
                DoneItems.Add(new NamedRow(d.Id, d.Text, (r, text) => Run(new EditDoneItem(r.Id, text)),
                    $"{board.Issues.Values.Count(i => i.DoneChecks.Contains(d.Id))} ticked"));
            DoneCheckMode = DoneCheckModes.First(m => m.Id == project.Settings.DoneCheck.ToString());

            var selected = SelectedWorkflow?.Id ?? board.DefaultWorkflow?.Id;
            Workflows.Clear();
            foreach (var w in board.Workflows.Values.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase))
                Workflows.Add(new NamedRow(w.Id, w.Name, (row, name) => Run(new RenameWorkflow(row.Id, name)),
                    board.DefaultWorkflow?.Id == w.Id ? "default" : null));
            SelectedWorkflow = Workflows.FirstOrDefault(w => w.Id == selected) ?? Workflows.FirstOrDefault();
            LoadWorkflow();

            Statuses.Clear();
            foreach (var s in project.Statuses)
            {
                var row = new NamedRow(s.Id, s.Name, (r, name) => Run(new RenameStatus(r.Id, name)),
                    $"{board.Issues.Values.Count(i => i.Status == s.Id)} issues")
                {
                    Choices = Categories,
                    Changed = (r, _) => Run(new SetStatusCategory(r.Id, ParseCategory(r.Choice?.Id))),
                };
                row.Loading();
                row.Choice = Categories.First(c => c.Id == s.Category.ToString());
                row.Loaded();
                Statuses.Add(row);
            }

            IssueTypes.Clear();
            IReadOnlyList<NamedItem> workflowChoices = [new NamedItem(null, "Default workflow"), .. board.Workflows.Values.Select(w => new NamedItem(w.Id, w.Name))];
            foreach (var t in project.IssueTypes)
            {
                var row = new NamedRow(t.Id, t.Name, (r, name) => Run(new EditIssueType(r.Id, name, r.Choice?.Id ?? "task")),
                    t.Level switch { IssueTypeLevel.Epic => "Epic level", IssueTypeLevel.Subtask => "Sub-task level", _ => "Standard level" })
                {
                    Choices = TypeIcons,
                    Choices2 = workflowChoices,
                    Changed = async (r, what) =>
                    {
                        if (what == nameof(NamedRow.Choice))
                            await Run(new EditIssueType(r.Id, r.Name, r.Choice?.Id ?? "task"));
                        else
                            await Run(new AssignWorkflow(r.Id, r.Choice2?.Id));
                    },
                };
                row.Loading();
                row.Choice = TypeIcons.FirstOrDefault(i => i.Id == t.Icon) ?? TypeIcons[2];
                row.Choice2 = workflowChoices.FirstOrDefault(w => w.Id == t.Workflow) ?? workflowChoices[0];
                row.Loaded();
                IssueTypes.Add(row);
            }

            Priorities.Clear();
            foreach (var p in project.Priorities)
            {
                var row = new NamedRow(p.Id, p.Name, (r, name) => Run(new EditPriority(r.Id, name, r.Choice?.Id ?? "medium")))
                {
                    Choices = PriorityIcons,
                    Changed = async (r, what) =>
                    {
                        if (what == nameof(NamedRow.Flag) && r.Flag)
                            await Run(new SetDefaultPriority(r.Id));
                        else if (what == nameof(NamedRow.Choice))
                            await Run(new EditPriority(r.Id, r.Name, r.Choice?.Id ?? "medium"));
                    },
                };
                row.Loading();
                row.Choice = PriorityIcons.FirstOrDefault(i => i.Id == p.Icon) ?? PriorityIcons[2];
                row.Flag = project.DefaultPriorityId == p.Id;
                row.Loaded();
                Priorities.Add(row);
            }

            Resolutions.Clear();
            foreach (var r in project.Resolutions)
                Resolutions.Add(new NamedRow(r.Id, r.Name, (row, name) => Run(new RenameResolution(row.Id, name))));

            CustomFields.Clear();
            foreach (var f in project.CustomFields)
            {
                var row = new NamedRow(f.Id, f.Name, (r, name) => Run(new EditCustomField(r.Id, name, r.Second.Split(','))),
                    f.Type switch { FieldType.Select => "Choice", _ => f.Type.ToString() })
                {
                    Changed = (r, what) => what == nameof(NamedRow.Second) && f.Type == FieldType.Select
                        ? Run(new EditCustomField(r.Id, r.Name, r.Second.Split(',')))
                        : Task.CompletedTask,
                };
                row.Loading();
                row.Flag = f.Type == FieldType.Select;   // shows the choices box
                row.Second = string.Join(", ", f.Options);
                row.Loaded();
                CustomFields.Add(row);
            }

            Components.Clear();
            foreach (var c in project.Components)
            {
                var row = new NamedRow(c.Id, c.Name, (r, name) => Run(new EditComponent(r.Id, name, r.Second)))
                {
                    Changed = (r, _) => Run(new EditComponent(r.Id, r.Name, r.Second)),
                };
                row.Loading();
                row.Second = c.Description ?? "";
                row.Loaded();
                Components.Add(row);
            }

            Versions.Clear();
            foreach (var v in board.Versions.Values.OrderBy(v => v.Release ?? DateOnly.MaxValue).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var count = board.Issues.Values.Count(i => i.FixVersions.Contains(v.Id));
                var row = new NamedRow(v.Id, v.Name, (r, name) => SaveVersion(r, name), $"{count} issues")
                {
                    Changed = (r, _) => SaveVersion(r, r.Name),
                };
                row.Loading();
                row.Second = v.Release?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
                row.Third = v.Description;
                row.Flag = v.Released;
                row.Flag2 = v.Archived;
                row.Loaded();
                Versions.Add(row);
            }

            People.Clear();
            foreach (var p in project.People)
            {
                var row = new NamedRow(p.Id, p.Name, (r, name) => Run(new EditPerson(r.Id, name, r.Second)),
                    p.Id == _context.Me ? "you" : $"{board.Issues.Values.Count(i => i.Assignee == p.Id)} assigned")
                {
                    Changed = (r, what) => what == nameof(NamedRow.Third)
                        ? Run(new SetPersonRole(r.Id, r.Third))
                        : Run(new EditPerson(r.Id, r.Name, r.Second)),
                };
                row.Loading();
                row.Second = p.Email ?? "";
                row.Third = p.Role ?? "";
                row.Loaded();
                People.Add(row);
            }

            LinkTypes.Clear();
            foreach (var l in project.LinkTypes)
            {
                var row = new NamedRow(l.Id, l.Outward, (r, name) => Run(new EditLinkType(r.Id, name, r.Second)))
                {
                    Changed = (r, _) => Run(new EditLinkType(r.Id, r.Name, r.Second)),
                };
                row.Loading();
                row.Second = l.Inward;
                row.Loaded();
                LinkTypes.Add(row);
            }
        }
        finally
        {
            _reloading = false;
        }
    }

    private Task SaveVersion(NamedRow row, string name)
    {
        var version = Snapshot.Versions.GetValueOrDefault(row.Id);
        if (version is null)
            return Task.CompletedTask;
        DateOnly? release = null;
        if (row.Second.Trim().Length > 0)
        {
            if (DateOnly.TryParseExact(row.Second.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                || DateOnly.TryParse(row.Second.Trim(), CultureInfo.CurrentCulture, out d))
                release = d;
            else
            {
                _context.Status.Show("A release date looks like 2026-12-01.");
                return Task.CompletedTask;
            }
        }
        return Run(new EditVersion(row.Id, name, row.Third, version.Start, release, row.Flag, row.Flag2));
    }

    private static StatusCategory ParseCategory(string? id) => Enum.TryParse<StatusCategory>(id, out var c) ? c : StatusCategory.ToDo;

    private static string CategoryName(StatusCategory? category) => category switch
    {
        StatusCategory.Done => "Done",
        StatusCategory.InProgress => "In Progress",
        _ => "To Do",
    };
}
