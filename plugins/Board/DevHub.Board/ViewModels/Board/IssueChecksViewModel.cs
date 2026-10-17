using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Services.Board;

namespace DevHub.ViewModels.Board;

/// <summary>
/// One line with a check box in the issue view: an acceptance criterion (editable, reorderable) or a Definition of
/// Done item (ticked only). Ticking runs the operation at once.
/// </summary>
public sealed partial class CheckRowViewModel : ObservableObject
{
    private readonly Func<CheckRowViewModel, bool, Task> _toggle;

    public CheckRowViewModel(string id, string text, bool isChecked, bool canEdit, Func<CheckRowViewModel, bool, Task> toggle)
    {
        Id = id;
        Text = text;
        _isChecked = isChecked;
        CanEdit = canEdit;
        _toggle = toggle;
    }

    public string Id { get; }
    public string Text { get; }
    public bool CanEdit { get; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _draft = "";

    public IRelayCommand? EditCommand { get; init; }
    public IAsyncRelayCommand? SaveCommand { get; init; }
    public IAsyncRelayCommand? RemoveCommand { get; init; }
    public IAsyncRelayCommand? MoveUpCommand { get; init; }
    public IAsyncRelayCommand? MoveDownCommand { get; init; }

    partial void OnIsCheckedChanged(bool value) => _ = _toggle(this, value);

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    public override string ToString() => Text;
}

/// <summary>
/// The acceptance criteria and Definition of Done of one issue (the issue view's two check lists). Rebuilt from the
/// issue after every change (<see cref="Update"/>).
/// </summary>
public sealed partial class IssueChecksViewModel(BoardContext context, string issueId) : ObservableObject
{
    public ObservableCollection<CheckRowViewModel> Criteria { get; } = [];
    public ObservableCollection<CheckRowViewModel> DoneItems { get; } = [];

    [ObservableProperty] private string _criteriaProgress = "";
    [ObservableProperty] private string _doneProgress = "";
    [ObservableProperty] private bool _showDefinitionOfDone;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private string _readyText = "";

    [NotifyCanExecuteChangedFor(nameof(AddCriterionCommand))]
    [ObservableProperty] private string _newCriterion = "";

    public void Update(Issue issue, BoardSnapshot board)
    {
        var project = board.Project;
        Criteria.Clear();
        for (var i = 0; i < issue.Criteria.Count; i++)
        {
            var item = issue.Criteria[i];
            var index = i;
            CheckRowViewModel? row = null;
            row = new CheckRowViewModel(item.Id, item.Text, item.Done, canEdit: true,
                (r, done) => context.RunAsync(new CheckCriterion(issueId, r.Id, done)))
            {
                EditCommand = new RelayCommand(() => { row!.Draft = row.Text; row.IsEditing = true; }),
                SaveCommand = new AsyncRelayCommand(async () =>
                {
                    if (row!.Draft.Trim() == row.Text || await context.RunAsync(new EditCriterion(issueId, row.Id, row.Draft)))
                        row.IsEditing = false;
                }),
                RemoveCommand = new AsyncRelayCommand(() => context.RunAsync(new RemoveCriterion(issueId, item.Id))),
                MoveUpCommand = new AsyncRelayCommand(() => context.RunAsync(new MoveCriterion(issueId, item.Id, index - 1)), () => index > 0),
                MoveDownCommand = new AsyncRelayCommand(() => context.RunAsync(new MoveCriterion(issueId, item.Id, index + 1)),
                    () => index < issue.Criteria.Count - 1),
            };
            Criteria.Add(row);
        }
        var met = issue.Criteria.Count(c => c.Done);
        CriteriaProgress = issue.Criteria.Count == 0 ? "" : $"{met} of {issue.Criteria.Count} met";

        DoneItems.Clear();
        ShowDefinitionOfDone = project.DefinitionOfDone.Count > 0 && project.IssueType(issue.Type)?.Level == IssueTypeLevel.Standard;
        if (ShowDefinitionOfDone)
        {
            foreach (var item in project.DefinitionOfDone)
                DoneItems.Add(new CheckRowViewModel(item.Id, item.Text, issue.DoneChecks.Contains(item.Id), canEdit: false,
                    (r, done) => context.RunAsync(new CheckDoneItem(issueId, r.Id, done))));
        }
        var ticked = project.DefinitionOfDone.Count(d => issue.DoneChecks.Contains(d.Id));
        DoneProgress = ShowDefinitionOfDone ? $"{ticked} of {project.DefinitionOfDone.Count}" : "";

        var missing = DoneReadiness.Missing(project, issue).Count;
        IsReady = missing == 0 && (issue.Criteria.Count > 0 || ShowDefinitionOfDone);
        ReadyText = missing == 0 ? "Ready for Done" : $"{missing} left before Done";
    }

    private bool CanAddCriterion() => !string.IsNullOrWhiteSpace(NewCriterion);

    [RelayCommand(CanExecute = nameof(CanAddCriterion))]
    private async Task AddCriterionAsync()
    {
        if (await context.RunAsync(new AddCriterion(issueId, NewCriterion)))
            NewCriterion = "";
    }

    /// <summary>Ticks every Definition of Done item at once.</summary>
    [RelayCommand]
    private async Task TickAllDoneAsync()
    {
        foreach (var row in DoneItems.Where(r => !r.IsChecked).ToList())
            await context.RunAsync(new CheckDoneItem(issueId, row.Id, true));
    }
}
