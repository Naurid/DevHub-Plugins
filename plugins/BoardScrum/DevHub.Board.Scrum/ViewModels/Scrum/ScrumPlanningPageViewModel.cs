using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board;
using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.ViewModels.Board;
using DevHub.ViewModels.Dialogs;
using DevHub.Sdk.Core.Localization;

namespace DevHub.ViewModels.Scrum;

/// <summary>On Scrum boards, the Backlog becomes sprint planning: a list per open sprint above the backlog.</summary>
public sealed class ScrumPlanningPageProvider : IBoardPageProvider
{
    public string Id => "scrum-planning";
    public string Title => L.T("Backlog");
    public int Order => 200;
    public string? Replaces => BacklogPageProvider.PageId;
    public bool AppliesTo(BoardDefinition definition) => definition.Kind == BoardKind.Scrum;
    public BoardPageViewModel Create(BoardPageHost host) => new ScrumPlanningPageViewModel(host);
}

/// <summary>
/// Sprint planning: the running sprint, the planned (future) sprints in order, then the backlog. Drag issues between
/// them to plan (and up and down to rank). Each sprint's header: dates, points against capacity, and its buttons.
/// </summary>
public sealed class ScrumPlanningPageViewModel : PlanningPageViewModel
{
    private readonly IssueListViewModel _backlog = new("backlog", L.T("Backlog")) { EmptyText = L.T("Nothing left in the backlog.") };

    public ScrumPlanningPageViewModel(BoardPageHost host) : base(host)
    {
        PageActions.Add(new ListAction(L.T("Create Sprint"), new AsyncRelayCommand(CreateSprintAsync)) { ToolTip = L.T("Plan another sprint") });
        AddList(_backlog);
        Rebuild();
    }

    public override string Title => L.T("Backlog");

    protected override void FillLists(IReadOnlyList<Issue> issues, BoardSnapshot board)
    {
        var open = board.Sprints.Values.Where(s => s.State != SprintState.Closed)
            .OrderBy(s => s.State == SprintState.Active ? 0 : 1).ThenBy(s => s.Sequence).ToList();

        // One list per open sprint, kept (with its expanded state and selection) across changes.
        foreach (var gone in Lists.Where(l => l.Tag is Sprint s && open.All(o => o.Id != s.Id)).ToList())
            RemoveList(gone);
        var index = 0;
        foreach (var sprint in open)
        {
            var list = Lists.FirstOrDefault(l => l.Id == sprint.Id);
            if (list is null)
            {
                list = new IssueListViewModel(sprint.Id, sprint.Name) { EmptyText = L.T("Drag issues here to plan them for this sprint.") };
                AddList(list);
            }
            list.Tag = sprint;
            Lists.Move(Lists.IndexOf(list), index++);
            DescribeSprint(list, sprint, board);
            list.Fill(issues.Where(i => i.Sprint == sprint.Id), board, Context, Host.Definition.CardFields, this);
            Summarize(list, sprint);
        }
        Lists.Move(Lists.IndexOf(_backlog), Lists.Count - 1);
        _backlog.Fill(issues.Where(i => i.Sprint is null || !open.Any(s => s.Id == i.Sprint)), board, Context, Host.Definition.CardFields, this);
    }

    private void DescribeSprint(IssueListViewModel list, Sprint sprint, BoardSnapshot board)
    {
        list.Title = sprint.Name;
        var dates = sprint.Start is { } start && sprint.End is { } end
            ? $"{start.LocalDateTime:d MMM} – {end.LocalDateTime:d MMM}"
            : L.T("not started");
        list.Subtitle = sprint.State == SprintState.Active
            ? L.T($"Active · {dates}{(sprint.Goal.Length > 0 ? " · " + sprint.Goal : "")}")
            : sprint.Goal.Length > 0 ? sprint.Goal : "";
        list.Actions.Clear();
        if (sprint.State == SprintState.Active)
        {
            list.Actions.Add(new ListAction(L.T("Complete Sprint…"), new AsyncRelayCommand(() => CompleteAsync(sprint.Id))) { IsAccent = true });
        }
        else
        {
            var isNext = board.Sprints.Values.Where(s => s.State == SprintState.Future).OrderBy(s => s.Sequence).FirstOrDefault()?.Id == sprint.Id;
            if (isNext && board.Sprints.Values.All(s => s.State != SprintState.Active))
                list.Actions.Add(new ListAction(L.T("Start Sprint…"), new AsyncRelayCommand(() => StartAsync(sprint.Id))) { IsAccent = true });
            list.Actions.Add(new ListAction(L.T("Up"), new AsyncRelayCommand(() => Context.RunAsync(new MoveSprint(sprint.Id, -1)))) { ToolTip = L.T("Plan it earlier") });
            list.Actions.Add(new ListAction(L.T("Down"), new AsyncRelayCommand(() => Context.RunAsync(new MoveSprint(sprint.Id, 1)))) { ToolTip = L.T("Plan it later") });
            list.Actions.Add(new ListAction(L.T("Delete…"), new AsyncRelayCommand(() => DeleteAsync(sprint.Id))));
        }
        list.Actions.Add(new ListAction(L.T("Edit…"), new AsyncRelayCommand(() => EditAsync(sprint.Id))));
    }

    // "5 issues · 13 of 20 points" — the capacity next to the planned points, warning when it's over.
    private static void Summarize(IssueListViewModel list, Sprint sprint)
    {
        var points = list.Items.Where(i => i.Issue.Sprint == sprint.Id).Sum(i => i.Issue.StoryPoints ?? 0);
        if (sprint.Capacity is { } capacity)
        {
            var over = points > capacity ? L.T(" · over capacity") : "";
            list.Summary = L.T($"{list.Items.Count} issues · {points.ToString("0.##", CultureInfo.CurrentCulture)} of {capacity.ToString("0.##", CultureInfo.CurrentCulture)} points{over}");
        }
    }

    protected override IBoardOperation MoveOperation(IssueListDrop drop, string issueId, string? above, string? below) =>
        new PlanIssue(issueId, (drop.Target.Tag as Sprint)?.Id, above, below);

    protected override void OnDropping(IssueListDrop drop, IReadOnlyList<Issue> moving)
    {
        if (drop.Target.Tag is not Sprint { State: SprintState.Active } active)
            return;
        var joining = moving.Where(i => i.Sprint != active.Id).ToList();
        if (joining.Count > 0)
            Context.Status.Show(joining.Count == 1
                ? L.T($"{joining[0].Key} joins the running sprint: its scope grows.")
                : L.T($"{joining.Count} issues join the running sprint: its scope grows."));
    }

    protected override IReadOnlyList<(string Field, FieldValue Value)> FieldsForNewIssue() => [];

    private async Task CreateSprintAsync() => await Context.RunAsync(new CreateSprint());

    private async Task StartAsync(string sprintId)
    {
        var sprint = Context.Snapshot.Sprints.GetValueOrDefault(sprintId);
        if (sprint is null)
            return;
        var dialog = new StartSprintDialogViewModel(sprint, Context.Snapshot);
        if (await Context.Dialogs.ShowDialogAsync(dialog))
            await Context.RunAsync(new StartSprint(sprintId, dialog.StartAt, dialog.EndAt) { Goal = dialog.Goal, Name = dialog.Name });
    }

    private async Task CompleteAsync(string sprintId)
    {
        var dialog = new CompleteSprintDialogViewModel(Context.Snapshot, sprintId);
        if (await Context.Dialogs.ShowDialogAsync(dialog))
        {
            if (await Context.RunAsync(new CompleteSprint(sprintId, dialog.MoveTo) { NextSprint = dialog.NextSprintId })
                && await Context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel(L.T("Sprint Retrospective"),
                    L.T("The sprint is closed. Hold its retrospective now: what went well, what to improve, and the actions the team takes?"),
                    L.T("Open Retrospective"), [], destructive: false)))
                Host.ShowPage(RetrospectivePageProvider.PageId);
        }
    }

    private async Task EditAsync(string sprintId)
    {
        var sprint = Context.Snapshot.Sprints.GetValueOrDefault(sprintId);
        if (sprint is null)
            return;
        var dialog = new EditSprintDialogViewModel(sprint, Context.Snapshot);
        if (await Context.Dialogs.ShowDialogAsync(dialog))
            await Context.RunAsync(new EditSprint(sprintId, dialog.Name, dialog.Goal, dialog.StartAt, dialog.EndAt, dialog.CapacityValue)
            {
                Availability = dialog.Availability,
                FocusFactor = dialog.FocusValue is 1 ? null : dialog.FocusValue,
            });
    }

    private async Task DeleteAsync(string sprintId)
    {
        var sprint = Context.Snapshot.Sprints.GetValueOrDefault(sprintId);
        if (sprint is null)
            return;
        var count = Context.Snapshot.Issues.Values.Count(i => i.Sprint == sprintId);
        var ok = await Context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel(L.T("Delete Sprint"),
            count == 0 ? L.T($"Delete {sprint.Name}?") : L.T($"Delete {sprint.Name}? Its {count} issue(s) go back to the backlog."), L.T("Delete"), [sprint.Name]));
        if (ok)
            await Context.RunAsync(new DeleteSprint(sprintId));
    }
}
