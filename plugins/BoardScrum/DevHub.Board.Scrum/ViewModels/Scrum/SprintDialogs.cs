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
public sealed partial class EditSprintDialogViewModel : DialogViewModelBase
{
    public EditSprintDialogViewModel(Sprint sprint)
    {
        _name = sprint.Name;
        _goal = sprint.Goal;
        _startDate = sprint.Start?.LocalDateTime.Date;
        _endDate = sprint.End?.LocalDateTime.Date;
        _capacity = sprint.Capacity?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
    }

    public override string Title => "Edit Sprint";
    public override string OkText => "Save";
    public override bool FitsContent => true;
    public override double Width => 480;
    public override double MinHeight => 0;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _goal;
    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private DateTime? _endDate;
    [ObservableProperty] private string _capacity;
    [ObservableProperty] private string? _error;

    public DateTimeOffset? StartAt => StartDate is { } s ? new DateTimeOffset(s) : null;
    public DateTimeOffset? EndAt => EndDate is { } e ? new DateTimeOffset(e.Date.AddHours(23).AddMinutes(59)) : null;
    public double? CapacityValue { get; private set; }

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
