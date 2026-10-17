using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Scrum;

/// <summary>Start Sprint: name, goal, length (1–4 weeks or custom), start and end dates.</summary>
public sealed partial class StartSprintDialogViewModel : DialogViewModelBase
{
    public StartSprintDialogViewModel(Sprint sprint, BoardSnapshot board)
    {
        _name = sprint.Name;
        _goal = sprint.Goal;
        _startDate = DateTime.Today;
        _length = Lengths[1];
        UpdateEnd();
        var issues = board.Issues.Values.Where(i => i.Sprint == sprint.Id && board.LevelOf(i) == IssueTypeLevel.Standard).ToList();
        var points = issues.Sum(i => i.StoryPoints ?? 0);
        Summary = $"{issues.Count} issues, {points.ToString("0.##", CultureInfo.CurrentCulture)} points"
                  + (issues.Count(i => i.StoryPoints is null) is var unestimated and > 0 ? $" ({unestimated} not estimated)" : "")
                  + (sprint.Capacity is { } c ? $" · capacity {c.ToString("0.##", CultureInfo.CurrentCulture)}" : "");
    }

    public override string Title => "Start Sprint";
    public override string OkText => "Start";
    public override bool FitsContent => true;
    public override double Width => 480;
    public override double MinHeight => 0;

    public IReadOnlyList<string> Lengths { get; } = ["1 week", "2 weeks", "3 weeks", "4 weeks", "Custom"];

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _goal;
    [ObservableProperty] private string _length;
    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private DateTime? _endDate;
    [ObservableProperty] private string? _error;
    public string Summary { get; }

    public DateTimeOffset StartAt => new(StartDate ?? DateTime.Today);
    public DateTimeOffset EndAt => new((EndDate ?? DateTime.Today).Date.AddHours(23).AddMinutes(59));

    partial void OnLengthChanged(string value) => UpdateEnd();
    partial void OnStartDateChanged(DateTime? value) => UpdateEnd();

    private void UpdateEnd()
    {
        if (Length == "Custom" || StartDate is null)
            return;
        var weeks = int.Parse(Length[..1], CultureInfo.InvariantCulture);
        EndDate = StartDate.Value.AddDays(7 * weeks - 1);
    }

    protected override Task<bool> ApplyChangesAsync()
    {
        Error = string.IsNullOrWhiteSpace(Name) ? "Give the sprint a name."
            : StartDate is null || EndDate is null ? "Choose the dates."
            : EndDate < StartDate ? "The sprint ends after it starts."
            : null;
        return Task.FromResult(Error is null);
    }
}

/// <summary>Edit Sprint: name, goal, dates, capacity.</summary>
/// <summary>One person's row in Edit Sprint's team availability: their name and available days (text, as typed).</summary>
public sealed partial class MemberDaysRow(string personId, string name, string days, Action changed) : ObservableObject
{
    public string PersonId { get; } = personId;
    public string Name { get; } = name;
    [ObservableProperty] private string _days = days;

    public double? Value => double.TryParse(Days.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : null;

    partial void OnDaysChanged(string value) => changed();

    public override string ToString() => Name;
}

/// <summary>
/// Edit Sprint: name, goal, dates, and capacity planning: each person's available days, a focus factor, the focused days
/// they add up to and a capacity suggested from what the last sprints completed per focused day (none for a new team).
/// </summary>
public sealed partial class EditSprintDialogViewModel : DialogViewModelBase
{
    private readonly BoardSnapshot _board;
    private readonly string _sprintId;
    private readonly int _typicalDays;
    private bool _loading = true;

    public EditSprintDialogViewModel(Sprint sprint, BoardSnapshot board)
    {
        _board = board;
        _sprintId = sprint.Id;
        _name = sprint.Name;
        _goal = sprint.Goal;
        _startDate = sprint.Start?.LocalDateTime.Date;
        _endDate = sprint.End?.LocalDateTime.Date;
        _capacity = sprint.Capacity?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        _focus = ((sprint.FocusFactor ?? 1) * 100).ToString("0", CultureInfo.CurrentCulture);
        _typicalDays = board.Sprints.Values.Where(s => s.Start is not null && s.End is not null).OrderByDescending(s => s.Start)
            .Select(s => SprintCapacity.WorkingDays(s.Start, s.End)).FirstOrDefault(d => d > 0) is var typical and > 0 ? typical : 10;
        var working = WorkingDays(sprint.Start, sprint.End);
        foreach (var person in board.Project.People.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var days = sprint.Availability.FirstOrDefault(a => a.Person == person.Id)?.Days
                       ?? (sprint.Availability.Count == 0 && working > 0 ? working : 0);
            Team.Add(new MemberDaysRow(person.Id, NamedItem.Person(person).Name, days.ToString("0.##", CultureInfo.CurrentCulture), UpdatePlan));
        }
        _loading = false;
        UpdatePlan();
    }

    public override string Title => "Edit Sprint";
    public override string OkText => "Save";
    public override bool FitsContent => true;
    public override double Width => 560;
    public override double MinHeight => 0;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _goal;
    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private DateTime? _endDate;
    [ObservableProperty] private string _capacity;
    [ObservableProperty] private string _focus;
    [ObservableProperty] private string? _error;

    /// <summary>"10 working days · 42 person-days · 31.5 focused days".</summary>
    [ObservableProperty] private string _planText = "";

    /// <summary>"Suggested: 24 points (0.76 points per focused day over the last 3 sprints)" or why there's none.</summary>
    [ObservableProperty] private string _suggestionText = "";

    [ObservableProperty] private double? _suggested;

    public System.Collections.ObjectModel.ObservableCollection<MemberDaysRow> Team { get; } = [];

    public DateTimeOffset? StartAt => StartDate is { } s ? new DateTimeOffset(s) : null;
    public DateTimeOffset? EndAt => EndDate is { } e ? new DateTimeOffset(e.Date.AddHours(23).AddMinutes(59)) : null;
    public double? CapacityValue { get; private set; }
    public IReadOnlyList<MemberDays> Availability => Team.Where(r => r.Value is > 0).Select(r => new MemberDays { Person = r.PersonId, Days = r.Value!.Value }).ToList();
    public double? FocusValue => double.TryParse(Focus.Trim().TrimEnd('%').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f is > 0 and <= 100 ? f / 100 : null;

    // Without dates yet: as long as the last sprint (a two-week one when there's none).
    private int WorkingDays(DateTimeOffset? start, DateTimeOffset? end) =>
        start is null || end is null ? _typicalDays : SprintCapacity.WorkingDays(start, end);

    partial void OnFocusChanged(string value) => UpdatePlan();
    partial void OnStartDateChanged(DateTime? value) => UpdatePlan();
    partial void OnEndDateChanged(DateTime? value) => UpdatePlan();

    private void UpdatePlan()
    {
        if (_loading)
            return;
        var working = WorkingDays(StartAt, EndAt);
        var personDays = Team.Sum(r => r.Value ?? 0);
        var focused = personDays * (FocusValue ?? 1);
        PlanText = $"{working} working days{(StartAt is null || EndAt is null ? " (no dates yet: as long as the last sprint)" : "")} · {personDays:0.##} person-days · {focused:0.##} focused days";
        var rate = SprintCapacity.PointsPerFocusedDay(_board, _sprintId);
        Suggested = SprintCapacity.Suggested(_board, focused, _sprintId);
        SuggestionText = Suggested is { } s
            ? $"Suggested: {s:0.##} points ({rate:0.##} points per focused day over the last sprints)"
            : rate is null
                ? "No suggestion yet: no finished sprint has availability recorded. Plan the first sprints low while the team gets going."
                : "Enter who is available to get a suggestion.";
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void UseSuggestion()
    {
        if (Suggested is { } s)
            Capacity = s.ToString("0.##", CultureInfo.CurrentCulture);
    }

    /// <summary>Every row back to the sprint's working days (or 0 without dates).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void FullTime()
    {
        var working = WorkingDays(StartAt, EndAt);
        foreach (var row in Team)
            row.Days = working.ToString(CultureInfo.CurrentCulture);
    }

    protected override Task<bool> ApplyChangesAsync()
    {
        Error = null;
        CapacityValue = null;
        if (Capacity.Trim().Length > 0)
        {
            if (!double.TryParse(Capacity.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var c) || c < 0)
                Error = "Capacity is a number of story points.";
            else
                CapacityValue = c;
        }
        if (Team.Any(r => r.Value is null))
            Error = "Available days are numbers (0 for someone who's away).";
        if (FocusValue is null)
            Error = "The focus factor is a percentage from 1 to 100.";
        if (string.IsNullOrWhiteSpace(Name))
            Error = "Give the sprint a name.";
        if (StartDate is { } s && EndDate is { } e && e < s)
            Error = "The sprint ends after it starts.";
        return Task.FromResult(Error is null);
    }
}

/// <summary>Complete Sprint: what got done, and where the unfinished issues go.</summary>
public sealed partial class CompleteSprintDialogViewModel : DialogViewModelBase
{
    private readonly IReadOnlyList<Sprint> _future;

    public CompleteSprintDialogViewModel(BoardSnapshot board, string sprintId)
    {
        var sprint = board.Sprints[sprintId];
        var issues = board.Issues.Values.Where(i => i.Sprint == sprintId && board.LevelOf(i) == IssueTypeLevel.Standard).ToList();
        var done = issues.Where(board.IsDone).ToList();
        var open = issues.Count - done.Count;
        SprintName = sprint.Name;
        Summary = $"{done.Count} of {issues.Count} issues done ({done.Sum(i => i.StoryPoints ?? 0).ToString("0.##", CultureInfo.CurrentCulture)} of "
                  + $"{issues.Sum(i => i.StoryPoints ?? 0).ToString("0.##", CultureInfo.CurrentCulture)} points; "
                  + $"{sprint.CommittedPoints.ToString("0.##", CultureInfo.CurrentCulture)} committed at the start).";
        HasOpen = open > 0;
        OpenText = open == 1 ? "1 issue isn't done. Move it to:" : $"{open} issues aren't done. Move them to:";
        _future = board.Sprints.Values.Where(s => s.State == SprintState.Future).OrderBy(s => s.Sequence).ToList();
        Targets = ["The backlog", .. _future.Select(s => s.Name), "A new sprint"];
        _target = _future.Count > 0 ? _future[0].Name : Targets[0];
    }

    public override string Title => "Complete Sprint";
    public override string OkText => "Complete";
    public override bool FitsContent => true;
    public override double Width => 480;
    public override double MinHeight => 0;

    public string SprintName { get; }
    public string Summary { get; }
    public bool HasOpen { get; }
    public string OpenText { get; }
    public IReadOnlyList<string> Targets { get; }
    [ObservableProperty] private string _target;

    public OpenIssuesGoTo MoveTo => Target == "The backlog" ? OpenIssuesGoTo.Backlog
        : Target == "A new sprint" ? OpenIssuesGoTo.NewSprint
        : OpenIssuesGoTo.NextSprint;

    public string? NextSprintId => _future.FirstOrDefault(s => s.Name == Target)?.Id;
}
