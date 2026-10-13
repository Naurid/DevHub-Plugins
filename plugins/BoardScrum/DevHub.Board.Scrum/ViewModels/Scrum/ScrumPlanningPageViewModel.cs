using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board;
using DevHub.Board.Core;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.ViewModels.Board;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Scrum;

/// <summary>On Scrum boards, the Backlog becomes sprint planning: a list per open sprint above the backlog.</summary>
public sealed class ScrumPlanningPageProvider : IBoardPageProvider
{
    public string Id => "scrum-planning";
    public string Title => "Backlog";
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
    private readonly IssueListViewModel _backlog = new("backlog", "Backlog") { EmptyText = "Nothing left in the backlog." };

    public ScrumPlanningPageViewModel(BoardPageHost host) : base(host)
    {
        PageActions.Add(new ListAction("Create Sprint", new AsyncRelayCommand(CreateSprintAsync)) { ToolTip = "Plan another sprint" });
        AddList(_backlog);
        Rebuild();
    }

    public override string Title => "Backlog";

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
                list = new IssueListViewModel(sprint.Id, sprint.Name) { EmptyText = "Drag issues here to plan them for this sprint." };
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
            : "not started";
        list.Subtitle = sprint.State == SprintState.Active
            ? $"Active · {dates}{(sprint.Goal.Length > 0 ? " · " + sprint.Goal : "")}"
            : sprint.Goal.Length > 0 ? sprint.Goal : "";
        list.Actions.Clear();
        if (sprint.State == SprintState.Active)
        {
            list.Actions.Add(new ListAction("Complete Sprint…", new AsyncRelayCommand(() => CompleteAsync(sprint.Id))) { IsAccent = true });
        }
        else
        {
            var isNext = board.Sprints.Values.Where(s => s.State == SprintState.Future).OrderBy(s => s.Sequence).FirstOrDefault()?.Id == sprint.Id;
            if (isNext && board.Sprints.Values.All(s => s.State != SprintState.Active))
                list.Actions.Add(new ListAction("Start Sprint…", new AsyncRelayCommand(() => StartAsync(sprint.Id))) { IsAccent = true });
            list.Actions.Add(new ListAction("Up", new AsyncRelayCommand(() => Context.RunAsync(new MoveSprint(sprint.Id, -1)))) { ToolTip = "Plan it earlier" });
            list.Actions.Add(new ListAction("Down", new AsyncRelayCommand(() => Context.RunAsync(new MoveSprint(sprint.Id, 1)))) { ToolTip = "Plan it later" });
            list.Actions.Add(new ListAction("Delete…", new AsyncRelayCommand(() => DeleteAsync(sprint.Id))));
        }
        list.Actions.Add(new ListAction("Edit…", new AsyncRelayCommand(() => EditAsync(sprint.Id))));
    }

    // "5 issues · 13 of 20 points" — the capacity next to the planned points, warning when it's over.
    private static void Summarize(IssueListViewModel list, Sprint sprint)
    {
        var points = list.Items.Where(i => i.Issue.Sprint == sprint.Id).Sum(i => i.Issue.StoryPoints ?? 0);
        if (sprint.Capacity is { } capacity)
        {
            var over = points > capacity ? " · over capacity" : "";
            list.Summary = $"{list.Items.Count} issues · {points.ToString("0.##", CultureInfo.CurrentCulture)} of {capacity.ToString("0.##", CultureInfo.CurrentCulture)} points{over}";
        }
    }

    protected override async Task DropAsync(IssueListDrop drop)
    {
        var sprint = (drop.Target.Tag as Sprint)?.Id;
        if (drop.Target.Tag is Sprint { State: SprintState.Active } active && drop.Source != drop.Target
            && Context.Snapshot.Issue(drop.Item.Id) is { } issue && issue.Sprint != active.Id)
            Context.Status.Show($"{issue.Key} joins the running sprint: its scope grows.");
        await Context.RunAsync(new PlanIssue(drop.Item.Id, sprint, drop.Above, drop.Below));
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
            await Context.RunAsync(new CompleteSprint(sprintId, dialog.MoveTo) { NextSprint = dialog.NextSprintId });
    }

    private async Task EditAsync(string sprintId)
    {
        var sprint = Context.Snapshot.Sprints.GetValueOrDefault(sprintId);
        if (sprint is null)
            return;
        var dialog = new EditSprintDialogViewModel(sprint);
        if (await Context.Dialogs.ShowDialogAsync(dialog))
            await Context.RunAsync(new EditSprint(sprintId, dialog.Name, dialog.Goal, dialog.StartAt, dialog.EndAt, dialog.CapacityValue));
    }

    private async Task DeleteAsync(string sprintId)
    {
        var sprint = Context.Snapshot.Sprints.GetValueOrDefault(sprintId);
        if (sprint is null)
            return;
        var count = Context.Snapshot.Issues.Values.Count(i => i.Sprint == sprintId);
        var ok = await Context.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Delete Sprint",
            count == 0 ? $"Delete {sprint.Name}?" : $"Delete {sprint.Name}? Its {count} issue(s) go back to the backlog.", "Delete", [sprint.Name]));
        if (ok)
            await Context.RunAsync(new DeleteSprint(sprintId));
    }
}
