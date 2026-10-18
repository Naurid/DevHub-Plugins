using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Sdk.Core.Localization;
using DevHub.Services.Board;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Board;

/// <summary>
/// The issue panel when several issues are selected: every field bulk edit can change, showing the value the issues
/// share or "—" where they differ. Changing a field sets it on all of them (one Undo), with bulk edit's rules.
/// </summary>
public sealed partial class MultiIssuePanelViewModel : ViewModelBase
{
    /// <summary>What a field shows when the selected issues don't agree.</summary>
    public const string Mixed = "—";

    private readonly BoardContext _context;

    public MultiIssuePanelViewModel(BoardContext context, IReadOnlyList<string> issueIds, Action close)
    {
        _context = context;
        CloseCommand = new RelayCommand(close);
        var sprints = context.Snapshot.Sprints.Count > 0;
        string[] fields =
        [
            BulkEditDialogViewModel.Status, IssueFields.Type, IssueFields.Priority, IssueFields.Assignee, IssueFields.StoryPoints,
            IssueFields.Parent, .. (sprints ? [IssueFields.Sprint] : Array.Empty<string>()), IssueFields.Labels,
            IssueFields.FixVersions, IssueFields.Components, IssueFields.Start, IssueFields.Due,
            IssueFields.OriginalEstimate, IssueFields.RemainingEstimate, IssueFields.Reporter,
            .. context.Project.CustomFields.Select(f => IssueFields.Custom(f.Id)), IssueFields.Description,
        ];
        foreach (var id in fields)
            Fields.Add(new MultiFieldViewModel(this, id, LabelOf(context, id), BulkEditDialogViewModel.ChoicesFor(context, id), BulkEditDialogViewModel.HintFor(id))
            {
                IsMultiline = id == IssueFields.Description,
            });
        Show(issueIds);
    }

    public IReadOnlyList<string> IssueIds { get; private set; } = [];
    public ObservableCollection<MultiFieldViewModel> Fields { get; } = [];
    public RelayCommand CloseCommand { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _keys = "";

    /// <summary>Shows another selection (the panel stays, its values follow).</summary>
    public void Show(IReadOnlyList<string> issueIds)
    {
        IssueIds = issueIds;
        Refresh();
    }

    /// <summary>Reads the values again (the board changed).</summary>
    public void Refresh()
    {
        var issues = IssueIds.Select(_context.Snapshot.Issue).OfType<Issue>().ToList();
        Title = L.T($"{issues.Count} issues selected");
        Keys = string.Join(", ", issues.Take(12).Select(i => i.Key)) + (issues.Count > 12 ? ", …" : "");
        foreach (var field in Fields)
            field.Show(issues.Select(i => ValueOf(i, field.FieldId)).Distinct().ToList());
    }

    /// <summary>Sets a field on every selected issue; tells the status bar when it can't.</summary>
    internal async Task ApplyAsync(string fieldId, string? choiceId, string text)
    {
        var edit = new BulkEditDialogViewModel(_context, IssueIds);
        if (!await edit.ApplyFieldAsync(fieldId, choiceId, text) && edit.Error is { } error)
        {
            _context.Status.Show(error);
            Refresh();
        }
    }

    [RelayCommand]
    private Task MoreChangesAsync() => _context.Dialogs.ShowDialogAsync(new BulkEditDialogViewModel(_context, IssueIds));

    private static string LabelOf(BoardContext context, string fieldId) => fieldId switch
    {
        IssueFields.OriginalEstimate => L.T("Original estimate"),
        IssueFields.RemainingEstimate => L.T("Remaining estimate"),
        IssueFields.Reporter => L.T("Reporter"),
        IssueFields.Description => L.T("Description"),
        _ when BulkEditDialogViewModel.CustomOf(context, fieldId) is { } custom => custom.Name,
        BulkEditDialogViewModel.Status => L.T("Status"),
        IssueFields.Type => L.T("Type"),
        IssueFields.Priority => L.T("Priority"),
        IssueFields.Assignee => L.T("Assignee"),
        IssueFields.StoryPoints => L.T("Story points"),
        IssueFields.Parent => L.T("Epic"),
        IssueFields.Sprint => L.T("Sprint"),
        IssueFields.Labels => L.T("Labels"),
        IssueFields.FixVersions => L.T("Fix versions"),
        IssueFields.Components => L.T("Components"),
        IssueFields.Start => L.T("Start date"),
        _ => L.T("Due date"),
    };

    /// <summary>An issue's value for a field, as a choice id or as the text the field shows.</summary>
    private string? ValueOf(Issue issue, string fieldId)
    {
        var snapshot = _context.Snapshot;
        return fieldId switch
        {
            BulkEditDialogViewModel.Status => issue.Status,
            IssueFields.Type => issue.Type,
            IssueFields.Priority => issue.Priority,
            IssueFields.Assignee => issue.Assignee,
            IssueFields.Parent => issue.Parent,
            IssueFields.Sprint => issue.Sprint,
            IssueFields.StoryPoints => issue.StoryPoints?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
            IssueFields.Labels => string.Join(" ", issue.Labels),
            IssueFields.FixVersions => string.Join(", ", issue.FixVersions.Select(v => snapshot.Versions.TryGetValue(v, out var version) ? version.Name : v)),
            IssueFields.Components => string.Join(", ", issue.Components.Select(c => snapshot.Project.Components.FirstOrDefault(x => x.Id == c)?.Name ?? c)),
            IssueFields.OriginalEstimate => issue.Time.OriginalMinutes is { } o ? WorkDuration.Format(o, snapshot.Project.Settings) : "",
            IssueFields.RemainingEstimate => issue.Time.RemainingMinutes is { } r ? WorkDuration.Format(r, snapshot.Project.Settings) : "",
            IssueFields.Reporter => issue.Reporter,
            IssueFields.Description => issue.Description,
            _ when fieldId.StartsWith(IssueFields.CustomPrefix, StringComparison.Ordinal) => issue.Custom.GetValueOrDefault(fieldId[IssueFields.CustomPrefix.Length..]),
            IssueFields.Start => issue.Start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            _ => issue.Due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
        };
    }
}

/// <summary>One field of the multi-selection panel: a choice or a text, "—" when the issues differ.</summary>
public sealed partial class MultiFieldViewModel : ObservableObject
{
    private static readonly NamedItem MixedChoice = new("\u0000mixed", MultiIssuePanelViewModel.Mixed);

    private readonly MultiIssuePanelViewModel _panel;
    private readonly IReadOnlyList<NamedItem> _choices;
    private bool _refreshing;
    private string _shownText = "";

    public MultiFieldViewModel(MultiIssuePanelViewModel panel, string fieldId, string label, IReadOnlyList<NamedItem> choices, string hint)
    {
        _panel = panel;
        FieldId = fieldId;
        Label = label;
        _choices = choices;
        Hint = hint;
    }

    public string FieldId { get; }
    public string Label { get; }
    public string Hint { get; }
    public bool IsChoice => _choices.Count > 0;
    public bool IsText => !IsChoice && !IsMultiline;

    /// <summary>A long text (the description).</summary>
    public bool IsMultiline { get; init; }

    public ObservableCollection<NamedItem> Choices { get; } = [];

    [ObservableProperty] private NamedItem? _choice;
    [ObservableProperty] private string _text = "";

    /// <summary>The issues don't agree on this field (shown as "—").</summary>
    [ObservableProperty] private bool _isMixed;

    public string Placeholder => IsMixed ? MultiIssuePanelViewModel.Mixed : "";

    partial void OnIsMixedChanged(bool value) => OnPropertyChanged(nameof(Placeholder));

    /// <summary>Shows the distinct values the selected issues have.</summary>
    internal void Show(IReadOnlyList<string?> values)
    {
        _refreshing = true;
        try
        {
            IsMixed = values.Count > 1;
            if (IsChoice)
            {
                Choices.Clear();
                if (IsMixed)
                    Choices.Add(MixedChoice);
                foreach (var c in _choices)
                    Choices.Add(c);
                Choice = IsMixed ? MixedChoice : Choices.FirstOrDefault(c => c.Id == values.FirstOrDefault());
            }
            else
            {
                _shownText = IsMixed ? "" : values.FirstOrDefault() ?? "";
                Text = _shownText;
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    partial void OnChoiceChanged(NamedItem? value)
    {
        if (_refreshing || value is null || ReferenceEquals(value, MixedChoice))
            return;
        _ = _panel.ApplyAsync(FieldId, value.Id, "");
    }

    partial void OnTextChanged(string value)
    {
        // A mixed field left empty keeps each issue's own value; anything typed sets it on all.
        if (_refreshing || value == _shownText || (IsMixed && value.Length == 0))
            return;
        _shownText = value;
        _ = _panel.ApplyAsync(FieldId, null, value);
    }
}
