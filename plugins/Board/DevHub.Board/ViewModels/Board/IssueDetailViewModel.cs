using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board;
using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Sdk.Core.Formatting;
using DevHub.Services.Board;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Board;

/// <summary>A link as the issue view lists it: "blocks DEV-7 Summary".</summary>
public sealed record LinkRow(string Phrase, string LinkType, string OtherId, string OtherKey, string OtherSummary, string OtherStatus, bool OtherDone)
{
    public override string ToString() => $"{Phrase} {OtherKey} {OtherSummary}";
}

/// <summary>A link type direction for the "add link" drop-down ("blocks" or "is blocked by").</summary>
public sealed record LinkChoice(string LinkType, string Phrase, bool Inward)
{
    public override string ToString() => Phrase;
}

public sealed record CommentRow(string Id, string Author, string When, string Body, bool IsMine, bool Edited)
{
    public string WhenText => Edited ? When + " (edited)" : When;
    public override string ToString() => $"{Author}, {When}: {Body}";
}

public sealed record WorkLogRow(string Id, string Author, string When, string Spent, string Note, bool IsMine)
{
    public override string ToString() => $"{Author} logged {Spent} {When}";
}

public sealed record HistoryRow(string Who, string When, string Text)
{
    public override string ToString() => $"{Who} {Text} {When}";
}

public sealed record AttachmentRow(string Id, string FileName, string Size, string When)
{
    public override string ToString() => FileName;
}

/// <summary>A custom field in the issue view: a text box, or a drop-down for choices and people.</summary>
public sealed partial class CustomFieldRow : ObservableObject
{
    private readonly Func<CustomFieldRow, Task> _save;
    private bool _loading;

    public CustomFieldRow(FieldDefinition definition, string? value, IReadOnlyList<NamedItem> choices, Func<CustomFieldRow, Task> save)
    {
        Definition = definition;
        _save = save;
        Choices = choices;
        _loading = true;
        _text = value ?? "";
        _choice = choices.FirstOrDefault(c => c.Id == value) ?? choices.FirstOrDefault();
        _loading = false;
    }

    public FieldDefinition Definition { get; }
    public string Name => Definition.Name;
    public IReadOnlyList<NamedItem> Choices { get; }
    public bool IsChoice => Choices.Count > 0;
    public string Placeholder => Definition.Type switch { FieldType.Number => "a number", FieldType.Date => "2026-10-31", _ => "" };

    [ObservableProperty] private string _text;
    [ObservableProperty] private NamedItem? _choice;

    public string? Value => IsChoice ? Choice?.Id : string.IsNullOrWhiteSpace(Text) ? null : Text.Trim();

    partial void OnTextChanged(string value)
    {
        if (!_loading && !IsChoice)
            _ = _save(this);
    }

    partial void OnChoiceChanged(NamedItem? value)
    {
        if (!_loading && IsChoice)
            _ = _save(this);
    }
}

/// <summary>
/// One issue, every field editable in place (the side panel next to a board, and the issue tab). Edits are saved
/// as they're made (text fields when they lose focus), each through an operation; the view refreshes from the
/// board afterwards, so what you see is always what's stored.
/// </summary>
public sealed partial class IssueDetailViewModel : ViewModelBase, IDisposable, ICardHost
{
    private readonly BoardContext _context;
    private readonly BoardUiServices _services;
    private readonly List<IssueSectionViewModel> _sections = [];
    private bool _loading;
    private Issue _issue;

    public IssueDetailViewModel(BoardContext context, string issueId, BoardUiServices services, bool isPanel)
    {
        _context = context;
        _services = services;
        IssueId = issueId;
        IsPanel = isPanel;
        _issue = context.Snapshot.Issue(issueId)!;
        foreach (var provider in services.Sections)
        {
            if (provider.Create(context, issueId) is { } section)
                _sections.Add(section);
        }
        Sections = _sections;
        Refresh();
    }

    public string IssueId { get; }
    public bool IsPanel { get; }
    public BoardContext Context => _context;

    /// <summary>Opens this (or another) issue in its own tab.</summary>
    public Action<string>? OpenInTab { get; init; }

    /// <summary>Closes the panel.</summary>
    public Action? Close { get; init; }

    /// <summary>Shows another issue in the same place (a panel switches; a tab opens a tab).</summary>
    public Action<string>? Navigate { get; init; }

    System.Windows.Input.ICommand ICardHost.SelectCommand => GoToChildCommand;
    System.Windows.Input.ICommand ICardHost.OpenCommand => _openChild ??= new RelayCommand<IssueItemViewModel?>(c => { if (c is not null) OpenInTab?.Invoke(c.Id); });
    private System.Windows.Input.ICommand? _openChild;

    public IReadOnlyList<IssueSectionViewModel> Sections { get; }
    public bool HasSections => Sections.Count > 0;

    // ---- Header -------------------------------------------------------------------------------------------

    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _typeIcon = "task";
    [ObservableProperty] private string _typeName = "";
    [ObservableProperty] private string? _parentKey;
    [ObservableProperty] private string? _parentSummary;
    [ObservableProperty] private string? _parentId;
    [ObservableProperty] private string? _epicKey;
    [ObservableProperty] private string _created = "";
    [ObservableProperty] private string _updated = "";

    // ---- Fields -------------------------------------------------------------------------------------------

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _isEditingDescription;
    [ObservableProperty] private string _descriptionDraft = "";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public ObservableCollection<NamedItem> Types { get; } = [];
    [ObservableProperty] private NamedItem? _type;

    public ObservableCollection<NamedItem> Statuses { get; } = [];
    [ObservableProperty] private NamedItem? _status;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isInProgress;
    public ObservableCollection<NamedItem> Resolutions { get; } = [];
    [ObservableProperty] private NamedItem? _resolution;

    public ObservableCollection<NamedItem> Priorities { get; } = [];
    [ObservableProperty] private NamedItem? _priority;

    public ObservableCollection<NamedItem> People { get; } = [];
    [ObservableProperty] private NamedItem? _assignee;
    [ObservableProperty] private NamedItem? _reporter;
    [ObservableProperty] private bool _isAssignedToMe;

    public ObservableCollection<NamedItem> Parents { get; } = [];
    [ObservableProperty] private NamedItem? _parent;
    [ObservableProperty] private bool _showParent;
    [ObservableProperty] private string _parentLabel = "Epic";

    public ObservableCollection<NamedItem> SprintChoices { get; } = [];
    [ObservableProperty] private NamedItem? _sprint;
    [ObservableProperty] private bool _showSprint;

    [ObservableProperty] private string _points = "";
    [ObservableProperty] private bool _showPoints;
    [ObservableProperty] private string _originalEstimate = "";
    [ObservableProperty] private string _remainingEstimate = "";
    [ObservableProperty] private string _timeSpent = "";
    [ObservableProperty] private double _timeProgress;
    [ObservableProperty] private DateTime? _start;
    [ObservableProperty] private DateTime? _due;
    [ObservableProperty] private string _labels = "";
    [ObservableProperty] private string _components = "";
    [ObservableProperty] private string _fixVersions = "";
    [ObservableProperty] private bool _hasComponents;
    [ObservableProperty] private bool _hasVersions;

    public ObservableCollection<CustomFieldRow> CustomFields { get; } = [];
    public bool HasCustomFields => CustomFields.Count > 0;

    // ---- Children, links, attachments, activity -----------------------------------------------------------

    public ObservableCollection<IssueItemViewModel> Children { get; } = [];
    [ObservableProperty] private string _childrenTitle = "Sub-tasks";
    [ObservableProperty] private string? _childrenProgress;
    [ObservableProperty] private double _childrenDone;
    [ObservableProperty] private bool _canHaveChildren;
    [ObservableProperty] private string _newChildSummary = "";

    public ObservableCollection<LinkRow> Links { get; } = [];
    public ObservableCollection<LinkChoice> LinkChoices { get; } = [];
    [ObservableProperty] private LinkChoice? _newLinkType;
    [ObservableProperty] private string _newLinkKey = "";
    [ObservableProperty] private bool _isAddingLink;

    public ObservableCollection<AttachmentRow> Attachments { get; } = [];

    public ObservableCollection<CommentRow> Comments { get; } = [];
    public ObservableCollection<WorkLogRow> WorkLogs { get; } = [];
    public bool HasWorkLogs => WorkLogs.Count > 0;
    public ObservableCollection<HistoryRow> History { get; } = [];
    [ObservableProperty] private string _newComment = "";
    [ObservableProperty] private string? _editingCommentId;
    [ObservableProperty] private string _editingCommentText = "";

    /// <summary>Activity tab: 0 comments, 1 work log, 2 history.</summary>
    [ObservableProperty] private int _activityTab;

    public ObservableCollection<IssueAction> PluginActions { get; } = [];
    public bool HasPluginActions => PluginActions.Count > 0;

    /// <summary>Re-reads the issue from the board (after any change).</summary>
    public void Refresh()
    {
        if (_context.Snapshot.Issue(IssueId) is not { } issue)
            return;
        _issue = issue;
        _loading = true;
        try
        {
            Load(issue, _context.Snapshot);
        }
        finally
        {
            _loading = false;
        }
        foreach (var section in _sections)
            section.Update(issue, _context.Snapshot);
    }

    private void Load(Issue issue, BoardSnapshot board)
    {
        var project = board.Project;
        var type = project.IssueType(issue.Type);
        var level = type?.Level ?? IssueTypeLevel.Standard;

        Key = issue.Key;
        TypeIcon = type?.Icon ?? "task";
        TypeName = type?.Name ?? issue.Type;
        Summary = issue.Summary;
        Description = issue.Description;
        OnPropertyChanged(nameof(HasDescription));
        Created = "Created " + RelativeOrDate(issue.Created);
        Updated = "Updated " + RelativeOrDate(issue.Updated);

        var parent = board.Issue(issue.Parent);
        ParentId = parent?.Id;
        ParentKey = parent?.Key;
        ParentSummary = parent?.Summary;
        EpicKey = board.EpicOf(issue)?.Key;

        Fill(Types, project.IssueTypes.Where(t => t.Level == level || (level != IssueTypeLevel.Epic && t.Level != IssueTypeLevel.Epic
                && (t.Level == IssueTypeLevel.Subtask) == (level == IssueTypeLevel.Subtask)))
            .Select(t => new NamedItem(t.Id, t.Name, t.Icon)));
        Type = Types.FirstOrDefault(t => t.Id == issue.Type);

        // Status: the current one plus the moves its workflow allows.
        var workflow = board.WorkflowFor(issue.Type);
        Fill(Statuses, (workflow?.Statuses ?? []).Where(s => s == issue.Status || workflow!.Allows(issue.Status, s))
            .Select(s => new NamedItem(s, _context.StatusName(s))));
        if (Statuses.All(s => s.Id != issue.Status))
            Statuses.Insert(0, new NamedItem(issue.Status, _context.StatusName(issue.Status)));
        Status = Statuses.FirstOrDefault(s => s.Id == issue.Status);
        var category = board.CategoryOf(issue);
        IsDone = category == StatusCategory.Done;
        IsInProgress = category == StatusCategory.InProgress;
        Fill(Resolutions, project.Resolutions.Select(r => new NamedItem(r.Id, r.Name)));
        Resolution = Resolutions.FirstOrDefault(r => r.Id == issue.Resolution);

        Fill(Priorities, project.Priorities.Select(p => new NamedItem(p.Id, p.Name, p.Icon)));
        Priority = Priorities.FirstOrDefault(p => p.Id == issue.Priority);

        Fill(People, [new NamedItem(null, "Unassigned"), .. project.People.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new NamedItem(p.Id, p.Name))]);
        Assignee = People.FirstOrDefault(p => p.Id == issue.Assignee) ?? People[0];
        Reporter = People.FirstOrDefault(p => p.Id == issue.Reporter) ?? People[0];
        IsAssignedToMe = issue.Assignee is not null && issue.Assignee == _context.Me;

        ShowParent = level != IssueTypeLevel.Epic;
        ParentLabel = level == IssueTypeLevel.Subtask ? "Parent" : "Epic";
        var wanted = level == IssueTypeLevel.Subtask ? IssueTypeLevel.Standard : IssueTypeLevel.Epic;
        var parents = board.Issues.Values.Where(i => board.LevelOf(i) == wanted && i.Id != issue.Id && (!board.IsDone(i) || i.Id == issue.Parent))
            .OrderBy(i => i.Rank, StringComparer.Ordinal).Select(i => new NamedItem(i.Id, $"{i.Key} {i.Summary}")).ToList();
        Fill(Parents, level == IssueTypeLevel.Subtask ? parents : [new NamedItem(null, "No epic"), .. parents]);
        Parent = Parents.FirstOrDefault(p => p.Id == issue.Parent) ?? (level == IssueTypeLevel.Subtask ? null : Parents.FirstOrDefault());

        ShowSprint = board.Sprints.Count > 0 && level == IssueTypeLevel.Standard;
        Fill(SprintChoices, [new NamedItem(null, "Backlog"), .. board.Sprints.Values
            .Where(s => s.State != SprintState.Closed || s.Id == issue.Sprint).OrderBy(s => s.Sequence)
            .Select(s => new NamedItem(s.Id, s.State == SprintState.Active ? s.Name + " (active)" : s.Name))]);
        Sprint = SprintChoices.FirstOrDefault(s => s.Id == issue.Sprint) ?? SprintChoices[0];

        ShowPoints = level != IssueTypeLevel.Subtask;
        Points = issue.StoryPoints?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        OriginalEstimate = _context.Duration(issue.Time.OriginalMinutes);
        RemainingEstimate = issue.Time.RemainingMinutes is { } rm ? WorkDuration.Format(rm, project.Settings) : "";
        var spent = issue.Time.SpentMinutes + board.ChildrenOf(issue.Id).Sum(c => c.Time.SpentMinutes);
        TimeSpent = spent > 0 ? WorkDuration.Format(spent, project.Settings) + " logged" : "nothing logged";
        var total = spent + (issue.Time.RemainingMinutes ?? 0);
        TimeProgress = total > 0 ? (double)spent / total : 0;
        Start = issue.Start?.ToDateTime(TimeOnly.MinValue);
        Due = issue.Due?.ToDateTime(TimeOnly.MinValue);
        Labels = string.Join(" ", issue.Labels);
        Components = string.Join(", ", issue.Components.Select(c => project.Component(c)?.Name ?? c));
        FixVersions = string.Join(", ", issue.FixVersions.Select(v => board.Versions.GetValueOrDefault(v)?.Name ?? v));
        HasComponents = project.Components.Count > 0;
        HasVersions = board.Versions.Count > 0;

        CustomFields.Clear();
        foreach (var field in project.CustomFields)
        {
            IReadOnlyList<NamedItem> choices = field.Type switch
            {
                FieldType.Select => [new NamedItem(null, "None"), .. field.Options.Select(o => new NamedItem(o, o))],
                FieldType.Person => [new NamedItem(null, "Nobody"), .. project.People.Select(p => new NamedItem(p.Id, p.Name))],
                _ => [],
            };
            CustomFields.Add(new CustomFieldRow(field, issue.Custom.GetValueOrDefault(field.Id), choices, SaveCustomFieldAsync));
        }
        OnPropertyChanged(nameof(HasCustomFields));

        // Children: an epic's issues, or a standard issue's sub-tasks.
        CanHaveChildren = level != IssueTypeLevel.Subtask && project.IssueTypes.Any(t => t.Level == level - 1);
        ChildrenTitle = level == IssueTypeLevel.Epic ? "Issues in this epic" : "Sub-tasks";
        var children = board.ChildrenOf(issue.Id);
        Children.Clear();
        foreach (var child in children)
            Children.Add(new IssueItemViewModel(child, board, _context) { Host = this });
        var done = children.Count(board.IsDone);
        ChildrenProgress = children.Count == 0 ? null : $"{done} of {children.Count} done";
        ChildrenDone = children.Count == 0 ? 0 : (double)done / children.Count;

        Links.Clear();
        foreach (var link in issue.Links)
        {
            if (board.Issue(link.Target) is { } other && project.LinkType(link.Type) is { } lt)
                Links.Add(new LinkRow(lt.Outward, lt.Id, other.Id, other.Key, other.Summary, _context.StatusName(other.Status), board.IsDone(other)));
        }
        foreach (var (from, link) in board.IncomingLinks(issue.Id))
        {
            if (project.LinkType(link.Type) is { } lt)
                Links.Add(new LinkRow(lt.Inward, lt.Id, from.Id, from.Key, from.Summary, _context.StatusName(from.Status), board.IsDone(from)));
        }
        var choice = NewLinkType;
        Fill(LinkChoices, project.LinkTypes.SelectMany(lt => lt.Outward == lt.Inward
            ? [new LinkChoice(lt.Id, lt.Outward, false)]
            : new[] { new LinkChoice(lt.Id, lt.Outward, false), new LinkChoice(lt.Id, lt.Inward, true) }));
        NewLinkType = LinkChoices.FirstOrDefault(c => c == choice) ?? LinkChoices.FirstOrDefault();

        Attachments.Clear();
        foreach (var a in issue.Attachments)
            Attachments.Add(new AttachmentRow(a.Id, a.FileName, FileSize(a.Size), RelativeOrDate(a.Added)));

        Comments.Clear();
        foreach (var c in issue.Comments.OrderByDescending(c => c.Created))
            Comments.Add(new CommentRow(c.Id, _context.PersonName(c.Author), RelativeOrDate(c.Created), c.Body, c.Author == _context.Me, c.Edited is not null));
        WorkLogs.Clear();
        foreach (var w in issue.Time.Log.OrderByDescending(w => w.Started))
            WorkLogs.Add(new WorkLogRow(w.Id, _context.PersonName(w.Author), RelativeOrDate(w.Started),
                WorkDuration.Format(w.Minutes, project.Settings), w.Note, w.Author == _context.Me));
        OnPropertyChanged(nameof(HasWorkLogs));
        History.Clear();
        foreach (var h in board.HistoryOf(issue.Id).Reverse())
            History.Add(new HistoryRow(_context.PersonName(h.Who), RelativeOrDate(h.At), HistoryText.Describe(h, board, _context)));

        PluginActions.Clear();
        foreach (var provider in _services.IssueActions)
            foreach (var action in provider.GetActions(_context, issue))
                PluginActions.Add(action);
        OnPropertyChanged(nameof(HasPluginActions));
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list))
            return;
        target.Clear();
        foreach (var item in list)
            target.Add(item);
    }

    public static string RelativeOrDate(DateTimeOffset when) =>
        DateTimeOffset.Now - when < TimeSpan.FromDays(7) ? RelativeTime.Format(when, DateTimeOffset.Now) : when.LocalDateTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    private static string FileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };

    // ---- Saving edits -------------------------------------------------------------------------------------

    private Task Save(string field, FieldValue value) =>
        _loading ? Task.CompletedTask : SaveAndRefresh(new EditIssue(IssueId, field, value));

    private async Task SaveAndRefresh(IBoardOperation operation)
    {
        await _context.RunAsync(operation);
        Refresh();   // shows the stored value again when the edit was refused
    }

    partial void OnSummaryChanged(string value)
    {
        if (!_loading && value.Trim() != _issue.Summary)
            _ = Save(IssueFields.Summary, new FieldValue.Text(value));
    }

    partial void OnTypeChanged(NamedItem? value)
    {
        if (!_loading && value?.Id is { } id && id != _issue.Type)
            _ = Save(IssueFields.Type, new FieldValue.Text(id));
    }

    partial void OnStatusChanged(NamedItem? value)
    {
        if (!_loading && value?.Id is { } id && id != _issue.Status)
            _ = SaveAndRefresh(new TransitionIssue(IssueId, id));
    }

    partial void OnResolutionChanged(NamedItem? value)
    {
        if (!_loading && value?.Id is { } id && id != _issue.Resolution)
            _ = SaveAndRefresh(new TransitionIssue(IssueId, _issue.Status) { Resolution = id });
    }

    partial void OnPriorityChanged(NamedItem? value)
    {
        if (!_loading && value?.Id != _issue.Priority)
            _ = Save(IssueFields.Priority, new FieldValue.Text(value?.Id));
    }

    partial void OnAssigneeChanged(NamedItem? value)
    {
        if (!_loading && value?.Id != _issue.Assignee)
            _ = Save(IssueFields.Assignee, new FieldValue.Text(value?.Id));
    }

    partial void OnReporterChanged(NamedItem? value)
    {
        if (!_loading && value?.Id != _issue.Reporter)
            _ = Save(IssueFields.Reporter, new FieldValue.Text(value?.Id));
    }

    partial void OnParentChanged(NamedItem? value)
    {
        if (!_loading && value?.Id != _issue.Parent)
            _ = Save(IssueFields.Parent, new FieldValue.Text(value?.Id));
    }

    partial void OnSprintChanged(NamedItem? value)
    {
        if (!_loading && value?.Id != _issue.Sprint)
            _ = Save(IssueFields.Sprint, new FieldValue.Text(value?.Id));
    }

    partial void OnPointsChanged(string value)
    {
        if (_loading)
            return;
        var text = value.Trim().Replace(',', '.');
        if (text.Length == 0)
            _ = Save(IssueFields.StoryPoints, new FieldValue.Number(null));
        else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var points))
            _ = Save(IssueFields.StoryPoints, new FieldValue.Number(points));
        else
        {
            _context.Status.Show("Story points must be a number.");
            Refresh();
        }
    }

    partial void OnOriginalEstimateChanged(string value) => SaveDuration(value, IssueFields.OriginalEstimate);

    partial void OnRemainingEstimateChanged(string value) => SaveDuration(value, IssueFields.RemainingEstimate);

    private void SaveDuration(string text, string field)
    {
        if (_loading)
            return;
        if (text.Trim().Length == 0)
            _ = Save(field, new FieldValue.Minutes(null));
        else if (text.Trim() is "0" or "0m" or "0h")
            _ = Save(field, new FieldValue.Minutes(0));
        else if (WorkDuration.TryParse(text, _context.Project.Settings, out var minutes))
            _ = Save(field, new FieldValue.Minutes(minutes));
        else
        {
            _context.Status.Show("A duration looks like 2h, 1d 4h or 30m.");
            Refresh();
        }
    }

    partial void OnStartChanged(DateTime? value)
    {
        var date = value is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null;
        if (!_loading && date != _issue.Start)
            _ = Save(IssueFields.Start, new FieldValue.Date(date));
    }

    partial void OnDueChanged(DateTime? value)
    {
        var date = value is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null;
        if (!_loading && date != _issue.Due)
            _ = Save(IssueFields.Due, new FieldValue.Date(date));
    }

    partial void OnLabelsChanged(string value)
    {
        if (!_loading)
            _ = Save(IssueFields.Labels, new FieldValue.Items(value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)));
    }

    private Task SaveCustomFieldAsync(CustomFieldRow row)
    {
        if (_loading)
            return Task.CompletedTask;
        var value = row.Value;
        if (row.Definition.Type == FieldType.Number && value is not null)
            value = value.Replace(',', '.');
        if (value == _issue.Custom.GetValueOrDefault(row.Definition.Id))
            return Task.CompletedTask;
        return Save(IssueFields.Custom(row.Definition.Id), new FieldValue.Text(value));
    }

    [RelayCommand]
    private async Task AssignToMeAsync()
    {
        if (await IssueCommands.MeAsync(_context) is { } me)
            await SaveAndRefresh(new EditIssue(IssueId, IssueFields.Assignee, new FieldValue.Text(me)));
    }

    [RelayCommand]
    private async Task EditComponentsAsync()
    {
        var items = _context.Project.Components.Select(c => new ChecklistItem(c.Id, c.Name, _issue.Components.Contains(c.Id), c.Description)).ToList();
        var dialog = new ChecklistDialogViewModel($"Components of {Key}", "The parts of the product this issue is about.", items);
        if (await _context.Dialogs.ShowDialogAsync(dialog))
            await SaveAndRefresh(new EditIssue(IssueId, IssueFields.Components, new FieldValue.Items(dialog.CheckedIds)));
    }

    [RelayCommand]
    private async Task EditVersionsAsync()
    {
        var items = _context.Snapshot.Versions.Values.Where(v => !v.Archived || _issue.FixVersions.Contains(v.Id))
            .OrderBy(v => v.Release ?? DateOnly.MaxValue).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(v => new ChecklistItem(v.Id, v.Name, _issue.FixVersions.Contains(v.Id), v.Released ? "released" : v.Release?.ToString("d MMM yyyy", CultureInfo.CurrentCulture)))
            .ToList();
        var dialog = new ChecklistDialogViewModel($"Fix versions of {Key}", "The releases this issue is planned for.", items);
        if (await _context.Dialogs.ShowDialogAsync(dialog))
            await SaveAndRefresh(new EditIssue(IssueId, IssueFields.FixVersions, new FieldValue.Items(dialog.CheckedIds)));
    }

    // ---- Description --------------------------------------------------------------------------------------

    [RelayCommand]
    private void EditDescription()
    {
        DescriptionDraft = Description;
        IsEditingDescription = true;
    }

    [RelayCommand]
    private async Task SaveDescriptionAsync()
    {
        IsEditingDescription = false;
        if (DescriptionDraft != _issue.Description)
            await SaveAndRefresh(new EditIssue(IssueId, IssueFields.Description, new FieldValue.Text(DescriptionDraft)));
    }

    [RelayCommand]
    private void CancelDescription() => IsEditingDescription = false;

    // ---- Children -----------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task AddChildAsync()
    {
        var summary = NewChildSummary.Trim();
        if (summary.Length == 0)
            return;
        var snapshot = _context.Snapshot;
        var level = snapshot.LevelOf(_issue);
        var type = snapshot.Project.IssueTypes.FirstOrDefault(t => t.Level == level - 1 && (t.Id == "subtask" || t.Id == "story"))
                   ?? snapshot.Project.IssueTypes.FirstOrDefault(t => t.Level == level - 1);
        if (type is null)
            return;
        if (await _context.RunAsync(new CreateIssue(type.Id, summary) { Parent = IssueId }))
            NewChildSummary = "";
        Refresh();
    }

    [RelayCommand]
    private void GoTo(string? issueId)
    {
        if (issueId is not null)
            (Navigate ?? OpenInTab)?.Invoke(issueId);
    }

    [RelayCommand]
    private void GoToChild(IssueItemViewModel? child) => GoTo(child?.Id);

    [RelayCommand]
    private void GoToLink(LinkRow? link) => GoTo(link?.OtherId);

    // ---- Links --------------------------------------------------------------------------------------------

    [RelayCommand]
    private void StartAddLink() => IsAddingLink = true;

    [RelayCommand]
    private async Task AddLinkAsync()
    {
        if (NewLinkType is not { } type)
            return;
        var other = _context.Snapshot.IssueByKey(NewLinkKey.Trim());
        if (other is null)
        {
            _context.Status.Show($"There's no issue {NewLinkKey.Trim()} on this board.");
            return;
        }
        var ok = type.Inward
            ? await _context.RunAsync(new AddLink(other.Id, type.LinkType, IssueId))
            : await _context.RunAsync(new AddLink(IssueId, type.LinkType, other.Id));
        if (ok)
        {
            NewLinkKey = "";
            IsAddingLink = false;
        }
        Refresh();
    }

    [RelayCommand]
    private async Task RemoveLinkAsync(LinkRow? link)
    {
        if (link is not null)
            await SaveAndRefresh(new RemoveLink(IssueId, link.LinkType, link.OtherId));
    }

    // ---- Attachments --------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task AddAttachmentAsync()
    {
        var path = await _context.Dialogs.PickFileAsync("Attach a file", "All files", ["*"]);
        if (path is null)
            return;
        byte[] bytes;
        try
        {
            if (new FileInfo(path).Length > AddAttachment.MaxBytes)
            {
                _context.Status.Show($"Attachments can be up to {AddAttachment.MaxBytes / 1024 / 1024} MB.");
                return;
            }
            bytes = await File.ReadAllBytesAsync(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _context.Status.Show("The file couldn't be read: " + e.Message);
            return;
        }
        await SaveAndRefresh(new AddAttachment(IssueId, Path.GetFileName(path), bytes));
    }

    [RelayCommand]
    private void OpenAttachment(AttachmentRow? row)
    {
        var attachment = _issue.Attachments.FirstOrDefault(a => a.Id == row?.Id);
        if (attachment is null)
            return;
        if (_context.Snapshot.AttachmentBytes(_issue, attachment) is { } bytes)
            _services.OpenFile(attachment.FileName, bytes);
        else
            _context.Status.Show($"{attachment.FileName} is missing from the board's files.");
    }

    [RelayCommand]
    private async Task RemoveAttachmentAsync(AttachmentRow? row)
    {
        if (row is null)
            return;
        var ok = await _context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Remove Attachment",
            $"Remove {row.FileName} from {Key}?", "Remove", [row.FileName]));
        if (ok)
            await SaveAndRefresh(new RemoveAttachment(IssueId, row.Id));
    }

    // ---- Comments and work ----------------------------------------------------------------------------------

    [RelayCommand]
    private async Task AddCommentAsync()
    {
        if (string.IsNullOrWhiteSpace(NewComment))
            return;
        if (await _context.RunAsync(new AddComment(IssueId, NewComment)))
            NewComment = "";
        Refresh();
    }

    [RelayCommand]
    private void EditComment(CommentRow? row)
    {
        if (row is null)
            return;
        EditingCommentId = row.Id;
        EditingCommentText = row.Body;
    }

    [RelayCommand]
    private async Task SaveCommentAsync()
    {
        if (EditingCommentId is { } id)
            await SaveAndRefresh(new EditComment(IssueId, id, EditingCommentText));
        EditingCommentId = null;
    }

    [RelayCommand]
    private void CancelCommentEdit() => EditingCommentId = null;

    [RelayCommand]
    private async Task DeleteCommentAsync(CommentRow? row)
    {
        if (row is null)
            return;
        var ok = await _context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Delete Comment", "Delete this comment?", "Delete", [row.Body]));
        if (ok)
            await SaveAndRefresh(new DeleteComment(IssueId, row.Id));
    }

    [RelayCommand]
    private async Task LogWorkAsync()
    {
        await _context.Dialogs.ShowDialogAsync(new LogWorkDialogViewModel(_context, _issue));
        Refresh();
    }

    [RelayCommand]
    private async Task DeleteWorkLogAsync(WorkLogRow? row)
    {
        if (row is null)
            return;
        var ok = await _context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Delete Work Log",
            $"Delete {row.Spent} logged by {row.Author}? The time goes back onto the remaining estimate.", "Delete", [row.Note]));
        if (ok)
            await SaveAndRefresh(new DeleteWorkLog(IssueId, row.Id));
    }

    // ---- Header actions -----------------------------------------------------------------------------------

    [RelayCommand]
    private void OpenTab() => OpenInTab?.Invoke(IssueId);

    [RelayCommand]
    private void ClosePanel() => Close?.Invoke();

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (await IssueCommands.DeleteAsync(_context, IssueId))
            Close?.Invoke();
    }

    [RelayCommand]
    private async Task RunPluginActionAsync(IssueAction? action)
    {
        if (action is not null)
            await action.Run();
        Refresh();
    }

    [RelayCommand]
    private async Task CloneAsync()
    {
        var id = BoardIds.New();
        var fields = new List<(string, FieldValue)>
        {
            (IssueFields.Priority, new FieldValue.Text(_issue.Priority)),
            (IssueFields.Labels, new FieldValue.Items(_issue.Labels)),
            (IssueFields.Components, new FieldValue.Items(_issue.Components)),
            (IssueFields.FixVersions, new FieldValue.Items(_issue.FixVersions)),
            (IssueFields.StoryPoints, new FieldValue.Number(_issue.StoryPoints)),
        };
        if (await _context.RunAsync(new CreateIssue(_issue.Type, "Clone of " + _issue.Summary)
            {
                Id = id, Parent = _issue.Parent, Description = _issue.Description, Fields = fields,
            }))
        {
            if (_context.Project.LinkType("clones") is not null)
                await _context.RunAsync(new AddLink(id, "clones", IssueId));
            GoTo(id);
        }
    }

    public void Dispose()
    {
        foreach (var section in _sections)
            section.Dispose();
        _sections.Clear();
    }

    public override string ToString() => $"{Key} {Summary}";
}

/// <summary>Change-log lines as sentences ("changed Story points from 3 to 5").</summary>
public static class HistoryText
{
    public static string Describe(HistoryEntry entry, BoardSnapshot board, BoardContext context)
    {
        var project = board.Project;
        switch (entry.Field)
        {
            case "created":
                return "created the issue";
            case "comment":
                return entry.To is not null ? "added a comment" : "deleted a comment";
            case "worklog":
                return entry.To is not null ? $"logged {Minutes(entry.To)}" : $"deleted {Minutes(entry.From)} of logged work";
            case "attachment":
                return entry.To is not null ? $"attached {entry.To}" : $"removed {entry.From}";
            case "link":
                return entry.To is not null ? "added a link " + LinkText(entry.To) : "removed a link " + LinkText(entry.From);
            case "key":
                return $"moved it from {entry.From} to {entry.To}";
            case IssueFields.Description:
                return "changed the description";
        }
        var name = IssueFields.DisplayName(entry.Field, project);
        var from = Value(entry.Field, entry.From);
        var to = Value(entry.Field, entry.To);
        return (from, to) switch
        {
            (null, _) => $"set {name} to {to}",
            (_, null) => $"cleared {name} (was {from})",
            _ => $"changed {name} from {from} to {to}",
        };

        string Minutes(string? raw) => int.TryParse(raw, out var m) ? WorkDuration.Format(m, project.Settings) : raw ?? "";

        string LinkText(string? raw)
        {
            if (raw is null)
                return "";
            var inward = raw.Contains("<:");
            var parts = raw.Split(inward ? "<:" : ":");
            var type = project.LinkType(parts[0]);
            var other = parts.Length > 1 ? board.Issue(parts[1])?.Key ?? "a deleted issue" : "";
            return $"({(inward ? type?.Inward : type?.Outward) ?? parts[0]} {other})";
        }

        string? Value(string field, string? raw)
        {
            if (raw is null)
                return null;
            return field switch
            {
                IssueFields.Status => context.StatusName(raw),
                IssueFields.Type => context.TypeName(raw),
                IssueFields.Priority => context.PriorityName(raw),
                IssueFields.Resolution => project.Resolution(raw)?.Name ?? raw,
                IssueFields.Assignee or IssueFields.Reporter => context.PersonName(raw),
                IssueFields.Parent => board.Issue(raw)?.Key ?? "a deleted issue",
                IssueFields.Sprint => board.Sprints.GetValueOrDefault(raw)?.Name ?? raw,
                IssueFields.Components => string.Join(", ", raw.Split(',').Select(c => project.Component(c)?.Name ?? c)),
                IssueFields.FixVersions => string.Join(", ", raw.Split(',').Select(v => board.Versions.GetValueOrDefault(v)?.Name ?? v)),
                IssueFields.OriginalEstimate or IssueFields.RemainingEstimate => Minutes(raw),
                _ when field.StartsWith(IssueFields.CustomPrefix, StringComparison.Ordinal)
                       && project.CustomField(field[IssueFields.CustomPrefix.Length..])?.Type == FieldType.Person => context.PersonName(raw),
                _ => raw.Length > 80 ? raw[..80] + "…" : raw,
            };
        }
    }
}
