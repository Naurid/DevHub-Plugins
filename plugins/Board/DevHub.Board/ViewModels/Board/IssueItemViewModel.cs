using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Services.Board;

namespace DevHub.ViewModels.Board;

/// <summary>
/// One issue as a card or list row: the texts and icons it shows, read once from a snapshot. Cards are rebuilt
/// when the board changes (they're cheap), so this has no change notifications of its own.
/// </summary>
public sealed class IssueItemViewModel : ViewModelBase
{
    public IssueItemViewModel(Issue issue, BoardSnapshot board, BoardContext context, IReadOnlyList<string>? cardFields = null)
    {
        Issue = issue;
        var project = board.Project;
        var type = project.IssueType(issue.Type);
        var fields = cardFields ?? ["epic", "storyPoints", "priority", "assignee"];

        Id = issue.Id;
        Key = issue.Key;
        Summary = issue.Summary;
        TypeName = type?.Name ?? issue.Type;
        TypeIcon = type?.Icon ?? "task";
        StatusName = context.StatusName(issue.Status);
        StatusCategory = board.CategoryOf(issue);
        IsDone = StatusCategory == StatusCategory.Done;
        var priority = project.Priority(issue.Priority);
        PriorityName = priority?.Name ?? "";
        PriorityIcon = priority?.Icon;
        ShowPriority = fields.Contains("priority") && priority is not null;
        var assignee = project.Person(issue.Assignee);
        AssigneeName = assignee?.Name;
        ShowAssignee = fields.Contains("assignee") && assignee is not null;
        Points = issue.StoryPoints?.ToString("0.##", CultureInfo.CurrentCulture);
        ShowPoints = fields.Contains("storyPoints") && Points is not null;
        var epic = board.EpicOf(issue);
        EpicName = epic?.Summary;
        ShowEpic = fields.Contains("epic") && epic is not null;
        Labels = string.Join(" ", issue.Labels);
        ShowLabels = fields.Contains("labels") && issue.Labels.Count > 0;
        Due = issue.Due?.ToString("d MMM", CultureInfo.CurrentCulture);
        ShowDue = fields.Contains("due") && Due is not null;
        IsOverdue = issue.Due is { } due && !IsDone && due < DateOnly.FromDateTime(DateTime.Today);
        Remaining = issue.Time.RemainingMinutes is { } r ? context.Duration(r) : null;
        ShowRemaining = fields.Contains("remainingEstimate") && Remaining is not null;
        var subtasks = board.ChildrenOf(issue.Id).Where(c => board.LevelOf(c) == IssueTypeLevel.Subtask).ToList();
        SubtaskText = subtasks.Count == 0 ? null : $"{subtasks.Count(board.IsDone)}/{subtasks.Count}";
        CriteriaText = issue.Criteria.Count == 0 ? null : $"AC {issue.Criteria.Count(c => c.Done)}/{issue.Criteria.Count}";
        IsEpic = type?.Level == IssueTypeLevel.Epic;
        Updated = issue.Updated;
    }

    public Issue Issue { get; }
    public string Id { get; }
    public string Key { get; }
    public string Summary { get; }
    public string TypeName { get; }
    public string TypeIcon { get; }
    public string StatusName { get; }
    public StatusCategory StatusCategory { get; }
    public bool IsDone { get; }
    public bool IsEpic { get; }
    public string PriorityName { get; }
    public string? PriorityIcon { get; }
    public bool ShowPriority { get; }
    public string? AssigneeName { get; }
    public bool ShowAssignee { get; }
    public string? Points { get; }
    public bool ShowPoints { get; }
    public string? EpicName { get; }
    public bool ShowEpic { get; }
    public string Labels { get; }
    public bool ShowLabels { get; }
    public string? Due { get; }
    public bool ShowDue { get; }
    public bool IsOverdue { get; }
    public string? Remaining { get; }
    public bool ShowRemaining { get; }
    public string? SubtaskText { get; }

    /// <summary>"AC 2/3": acceptance criteria met, or null without criteria.</summary>
    public string? CriteriaText { get; }
    public DateTimeOffset Updated { get; }

    public bool IsToDo => StatusCategory == StatusCategory.ToDo;
    public bool IsInProgress => StatusCategory == StatusCategory.InProgress;

    /// <summary>The page the card is on (selecting, opening and the card menu go there).</summary>
    public ICardHost? Host { get; init; }

    public ICommand SelectCommand => _select ??= Forward(h => h.SelectCommand);
    public ICommand OpenCommand => _open ??= Forward(h => h.OpenCommand);
    public ICommand AssignToMeCommand => Forward(h => h.AssignToMeCommand);
    public ICommand DeleteCommand => Forward(h => h.DeleteCommand);
    public ICommand MoveToTopCommand => Forward(h => h.MoveToTopCommand);
    public ICommand MoveToBottomCommand => Forward(h => h.MoveToBottomCommand);
    public ICommand NextColumnCommand => Forward(h => h.NextColumnCommand);
    public ICommand PreviousColumnCommand => Forward(h => h.PreviousColumnCommand);
    public bool CanAssign => Host?.AssignToMeCommand is not null;
    public bool CanDelete => Host?.DeleteCommand is not null;
    public bool CanRank => Host?.MoveToTopCommand is not null;
    public bool CanMoveColumns => Host?.NextColumnCommand is not null;
    public ICommand? PickCommand => Host?.PickCommand is null ? null : _pick ??= Forward(h => h.PickCommand);
    private ICommand? _select, _open, _pick;

    /// <summary>Picked into a selection of several cards (Ctrl+click), to drag or edit together.</summary>
    public bool IsPicked
    {
        get => _isPicked;
        set => SetProperty(ref _isPicked, value);
    }
    private bool _isPicked;

    private RelayCommand Forward(Func<ICardHost, ICommand?> command) =>
        new(() => { if (Host is not null && command(Host) is { } c && c.CanExecute(this)) c.Execute(this); });

    public string ToolTip => $"{Key} · {TypeName} · {StatusName}\n{Summary}";

    public override string ToString() => $"{Key} {Summary}";
}
