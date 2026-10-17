using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Board.Timeline.Core;
using ReleaseList = DevHub.Board.Timeline.Core.Releases;
using DevHub.ViewModels.Board;
using DevHub.ViewModels.Dialogs;
using DevHub.Sdk.Core.Localization;

namespace DevHub.ViewModels.Timeline;

public sealed class TimelinePageProvider : IBoardPageProvider
{
    public string Id => "timeline";
    public string Title => L.T("Timeline");
    public int Order => 600;
    public BoardPageViewModel Create(BoardPageHost host) => new TimelinePageViewModel(host);
}

public sealed class ReleasesPageProvider : IBoardPageProvider
{
    public string Id => "releases";
    public string Title => L.T("Releases");
    public int Order => 650;
    public BoardPageViewModel Create(BoardPageHost host) => new ReleasesPageViewModel(host);
}

/// <summary>A roadmap row: the issue and its bar's position across the range.</summary>
public sealed class TimelineRowViewModel(TimelineItem item, TimelineResult range, double today, BoardPageHost host) : ViewModelBase
{
    public TimelineItem Item { get; } = item;
    public string Key => Item.Issue.Key;
    public string Summary => Item.Issue.Summary;
    public bool IsChild => Item.Depth > 0;
    public double Start { get; } = range.Fraction(item.Start);
    public double End { get; } = range.Fraction(item.End.AddDays(1));
    public double Today { get; } = today;
    public double Progress => Item.Progress;
    public string Dates => $"{Item.Start:d MMM} – {Item.End:d MMM yyyy}" + (Item.DatesGuessed ? L.T(" (estimated: set start and due dates)") : "");
    public string ToolTip => $"{Key} {Summary}\n{Dates}\n{Item.Progress:P0} done"
                             + (Item.IsLate ? L.T("\nLate: its due date has passed.") : "")
                             + (Item.BlockedBy.Count > 0 ? L.T("\nBlocked by ") + string.Join(", ", Item.BlockedBy.Select(b => b.Key)) : "")
                             + (Item.HasConflict ? L.T("\nStarts before a blocker is due.") : "");
    public string? BlockedText => Item.BlockedBy.Count == 0 ? null : L.T("blocked by ") + string.Join(", ", Item.BlockedBy.Select(b => b.Key));
    public IRelayCommand SelectCommand => new RelayCommand(() => host.SelectIssue(Item.Issue.Id));
    public IRelayCommand OpenCommand => new RelayCommand(() => host.OpenIssue(Item.Issue.Id));

    /// <summary>Create Issue in this epic (only on an epic's row; it starts with the epic's dates and labels).</summary>
    public IAsyncRelayCommand CreateInCommand => new AsyncRelayCommand(() => host.CreateIssue(new CreateIssueDefaults { Parent = Item.Issue.Id }));
    public override string ToString() => $"{Key} {Summary}, {Dates}";
}

/// <summary>
/// The roadmap: a bar per epic from its start to its due date (worked out from its issues when not set), progress,
/// late epics and blockers marked, today's line. Click a row to edit its dates in the issue panel, double-click to open it in a tab.
/// </summary>
public sealed partial class TimelinePageViewModel : BoardPageViewModel
{
    public TimelinePageViewModel(BoardPageHost host) : base(host)
    {
        Refresh();
    }

    public override string Title => L.T("Timeline");

    public ObservableCollection<TimelineRowViewModel> Rows { get; } = [];
    [ObservableProperty] private bool _withIssues;
    [ObservableProperty] private bool _showDone = true;
    [ObservableProperty] private DateTime _from;
    [ObservableProperty] private int _days;
    [ObservableProperty] private double _today;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _summary = "";

    partial void OnWithIssuesChanged(bool value) => Refresh();
    partial void OnShowDoneChanged(bool value) => Refresh();

    public override void OnBoardChanged() => Refresh();

    private void Refresh()
    {
        var board = Context.Snapshot;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var result = Roadmap.Build(board, today, WithIssues, ShowDone);
        From = result.From.ToDateTime(TimeOnly.MinValue);
        Days = result.Days;
        Today = result.Fraction(today);
        Rows.Clear();
        foreach (var item in result.Items)
            Rows.Add(new TimelineRowViewModel(item, result, Today, Host));
        IsEmpty = Rows.Count == 0;
        var epics = result.Items.Where(i => i.Depth == 0).ToList();
        Summary = epics.Count == 0 ? "" : L.T($"{epics.Count} epics · {epics.Count(e => e.IsLate)} late · {epics.Count(e => e.DatesGuessed)} without dates");
    }

    [RelayCommand]
    private async Task CreateEpicAsync() =>
        await Host.CreateIssue(new CreateIssueDefaults { Type = Context.Project.IssueTypes.FirstOrDefault(t => t.Level == IssueTypeLevel.Epic)?.Id });
}

/// <summary>A release in the list.</summary>
public sealed record ReleaseRow(ReleaseStatus Status)
{
    public string Id => Status.Version.Id;
    public string Name => Status.Version.Name;
    public string Detail => (Status.Version.Released ? L.T("Released") : Status.IsLate ? L.T("Late") : L.T("Unreleased"))
                            + (Status.Version.Release is { } d ? " · " + d.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "")
                            + $" · {Status.Done}/{Status.Issues.Count} done";
    public double Fraction => Status.Fraction;
    public bool IsLate => Status.IsLate;
    public override string ToString() => $"{Name}, {Detail}";
}

/// <summary>
/// Releases (fix versions): their progress, issues and release notes (Markdown, saved to a file); Release marks one
/// released and moves what isn't done to another version.
/// </summary>
public sealed partial class ReleasesPageViewModel : BoardPageViewModel, ICardHost
{
    public ReleasesPageViewModel(BoardPageHost host) : base(host)
    {
        Refresh();
    }

    public override string Title => L.T("Releases");

    System.Windows.Input.ICommand ICardHost.SelectCommand => SelectIssueCommand;
    System.Windows.Input.ICommand ICardHost.OpenCommand => OpenIssueCommand;

    public ObservableCollection<ReleaseRow> Releases { get; } = [];
    [ObservableProperty] private ReleaseRow? _selected;
    [ObservableProperty] private bool _showArchived;
    public ObservableCollection<IssueItemViewModel> Issues { get; } = [];
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _canRelease;

    partial void OnSelectedChanged(ReleaseRow? value) => ShowSelected();
    partial void OnShowArchivedChanged(bool value) => Refresh();

    public override void OnBoardChanged() => Refresh();

    private void Refresh()
    {
        var keep = Selected?.Id;
        Releases.Clear();
        foreach (var status in ReleaseList.All(Context.Snapshot, DateOnly.FromDateTime(DateTime.Today), ShowArchived))
            Releases.Add(new ReleaseRow(status));
        Selected = Releases.FirstOrDefault(r => r.Id == keep) ?? Releases.FirstOrDefault(r => !r.Status.Version.Released) ?? Releases.FirstOrDefault();
        ShowSelected();
    }

    private void ShowSelected()
    {
        var board = Context.Snapshot;
        Issues.Clear();
        HasSelection = Selected is not null;
        CanRelease = Selected is { Status.Version.Released: false };
        if (Selected is null)
        {
            Notes = "";
            return;
        }
        foreach (var issue in Selected.Status.Issues)
            Issues.Add(new IssueItemViewModel(issue, board, Context) { Host = this });
        Notes = ReleaseList.Notes(board, Selected.Status.Version, includeOpen: true);
    }

    [RelayCommand]
    private async Task NewVersionAsync()
    {
        var dialog = new TextInputDialogViewModel(L.T("New Version"), L.T("Name"), "", L.T("Create")) { SecondLabel = L.T("Release date (optional, like 2026-12-01)") };
        if (!await Context.Dialogs.ShowDialogAsync(dialog))
            return;
        DateOnly? release = DateOnly.TryParseExact(dialog.SecondText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                            || DateOnly.TryParse(dialog.SecondText.Trim(), CultureInfo.CurrentCulture, out d) ? d : null;
        var id = BoardIds.New();
        if (await Context.RunAsync(new CreateVersion(dialog.Text) { Id = id, Release = release }))
            Selected = Releases.FirstOrDefault(r => r.Id == id) ?? Selected;
    }

    [RelayCommand]
    private async Task ReleaseAsync()
    {
        if (Selected is not { } release)
            return;
        var others = Releases.Where(r => r.Id != release.Id && !r.Status.Version.Released).ToList();
        var open = release.Status.Open;
        var dialog = new TextInputDialogViewModel(L.T($"Release {release.Name}"), "", "", L.T("Release"))
        {
            ShowText = false,
            Explanation = open == 0 ? L.T($"Mark {release.Name} released?") : L.T($"{open} issue(s) in {release.Name} aren't done. Move them to:"),
            Choice = open == 0 ? null : new ChoiceInput(L.T("Move them to"), [L.T("No version"), .. others.Select(o => o.Name)], others.FirstOrDefault()?.Name ?? L.T("No version")),
        };
        if (!await Context.Dialogs.ShowDialogAsync(dialog))
            return;
        var target = others.FirstOrDefault(o => o.Name == dialog.Choice?.Selected)?.Id;
        await Context.RunAsync(new ReleaseVersion(release.Id, DateOnly.FromDateTime(DateTime.Today)) { MoveOpenTo = target });
    }

    [RelayCommand]
    private async Task SaveNotesAsync()
    {
        if (Selected is null)
            return;
        var path = await Context.Dialogs.PickSaveFileAsync(L.T("Save release notes"), $"release-notes-{BoardsSafe(Selected.Name)}.md", L.T("Markdown"), "md");
        if (path is null)
            return;
        try
        {
            await File.WriteAllTextAsync(path, Notes);
            Context.Status.Show(L.T($"Release notes saved to {path}."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Context.Status.Show(L.T("The release notes couldn't be saved: ") + e.Message);
        }
    }

    private static string BoardsSafe(string name) => new(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '-').ToArray());

    [RelayCommand]
    private void SelectIssue(IssueItemViewModel? item) => Host.SelectIssue(item?.Id);

    [RelayCommand]
    private void OpenIssue(IssueItemViewModel? item)
    {
        if (item is not null)
            Host.OpenIssue(item.Id);
    }
}
