using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Ci.Core;
using DevHub.Sdk.Core.Localization;
using DevHub.Services;
using DevHub.Services.Ci;
using DevHub.ViewModels.Dialogs;
using DevHub.ViewModels.Documents;
using DevHub.ViewModels.ToolWindows;

namespace DevHub.ViewModels.Ci;

/// <summary>
/// The CI tool window (bottom): the run history of the repository tab in front, filtered by branch and workflow, with
/// Load more; a run expands to its jobs (failed steps, Show Log, Re-run); Re-run failed jobs on a failed run.
/// </summary>
public sealed partial class CiToolWindowViewModel : ToolWindowViewModelBase, IDisposable
{
    private readonly IDialogService _dialogs;
    private readonly IServiceProvider _provider;

    public CiToolWindowViewModel(CiService service, IDialogService dialogs, IServiceProvider provider)
    {
        Service = service;
        _dialogs = dialogs;
        _provider = provider;
        // Unsubscribed in Dispose (DI disposes singletons at shutdown).
        Service.RunsChanged += OnRunsChanged;
        Service.ActiveChanged += OnActiveChanged;
    }

    public CiService Service { get; }

    public override string Id => "ci";
    public override string Title => L.T("CI");
    public override string IconKind => Controls.IconKind.Run;
    public override ToolWindowSlot Slot => ToolWindowSlot.Bottom;
    public override int Order => 110;   // after the Git Console

    /// <summary>The watched repository in front, or null.</summary>
    public CiRepository? Current { get; private set; }

    public ObservableCollection<RunRowViewModel> Runs { get; } = [];
    public ObservableCollection<FilterItem> Branches { get; } = [];
    public ObservableCollection<FilterItem> Workflows { get; } = [];

    [ObservableProperty] private FilterItem? _branch;
    [ObservableProperty] private FilterItem? _workflow;
    [ObservableProperty] private string? _repositoryText;
    [ObservableProperty] private string? _emptyTitle;
    [ObservableProperty] private string? _emptyDescription;
    [ObservableProperty] private bool _hasRuns;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _isLoadingMore;

    // Set by ShowRuns itself while it fills the filters: not a choice to act on.
    private bool _fillingFilters;

    partial void OnBranchChanged(FilterItem? value)
    {
        if (!_fillingFilters)
            ShowRuns();
    }

    partial void OnWorkflowChanged(FilterItem? value)
    {
        if (!_fillingFilters)
            ShowRuns();
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Shown: the repository in front, straight away.
        if (e.PropertyName == nameof(IsOpen) && IsOpen)
            Show();
    }

    private void OnActiveChanged(object? sender, EventArgs e) => Show();

    private void OnRunsChanged(object? sender, string folder)
    {
        if (Current is { } repo && string.Equals(repo.Folder, folder, StringComparison.OrdinalIgnoreCase))
            ShowRuns();
        else if (Current is null)
            Show();
    }

    /// <summary>Switches to the repository tab in front (its filters start at "all").</summary>
    private void Show()
    {
        var repo = Service.Repository(Service.ActiveFolder);
        if (!ReferenceEquals(repo, Current))
        {
            Current = repo;
            Runs.Clear();
            _fillingFilters = true;
            Branch = Workflow = null;
            _fillingFilters = false;
            if (repo is not null && !repo.Loaded)
                Service.PollSoon(repo);
        }
        ShowRuns();
    }

    private void ShowRuns()
    {
        var repo = Current;
        RepositoryText = repo?.Slug;
        if (repo is null)
        {
            SetEmpty(Service.ActiveFolder is null
                    ? L.T("No repository in front")
                    : L.T("Not a GitHub repository"),
                Service.ActiveFolder is null
                    ? L.T("Open a repository tab: its GitHub Actions runs show here.")
                    : L.T("The repository in front has no \"origin\" remote on github.com."));
            return;
        }
        if (repo.Problem is { } problem)
        {
            SetEmpty(L.T("Can't show the runs"), problem);
            return;
        }
        if (!repo.Loaded)
        {
            SetEmpty(L.T("Asking GitHub…"), repo.Slug);
            return;
        }

        _fillingFilters = true;
        try
        {
            Branch = UpdateFilter(Branches, repo.Runs.Select(r => r.Branch), L.T("All branches"), Branch);
            Workflow = UpdateFilter(Workflows, repo.Runs.Select(r => r.Workflow), L.T("All workflows"), Workflow);
        }
        finally
        {
            _fillingFilters = false;
        }
        var shown = repo.Runs.Where(r => (Branch?.Value is null || r.Branch == Branch.Value)
                                         && (Workflow?.Value is null || r.Workflow == Workflow.Value)).ToList();

        // Rows are kept by run (an expanded run stays expanded across polls), updated, reordered.
        var rows = Runs.ToDictionary(r => r.Id);
        var wanted = shown.Select(run =>
        {
            if (rows.TryGetValue(run.Id, out var row))
            {
                if (row.Run != run)
                    row.Run = run;
                return row;
            }
            return new RunRowViewModel(this, run);
        }).ToList();
        for (var i = Runs.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Runs[i]))
                Runs.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Runs.Count && ReferenceEquals(Runs[i], wanted[i]))
                continue;
            var at = Runs.IndexOf(wanted[i]);
            if (at >= 0)
                Runs.Move(at, i);
            else
                Runs.Insert(i, wanted[i]);
        }

        HasRuns = Runs.Count > 0;
        HasMore = repo.HasMore;
        if (!HasRuns)
            SetEmpty(L.T("No runs"), repo.Runs.Count == 0
                ? L.T($"{repo.Slug} has no GitHub Actions runs yet.")
                : L.T("No run matches the branch and workflow chosen."));
        else
            (EmptyTitle, EmptyDescription) = (null, null);
    }

    private void SetEmpty(string title, string description)
    {
        Runs.Clear();
        HasRuns = false;
        HasMore = false;
        (EmptyTitle, EmptyDescription) = (title, description);
    }

    // "All …" first, then each value; the chosen one stays chosen while it's still there.
    private static FilterItem UpdateFilter(ObservableCollection<FilterItem> items, IEnumerable<string> values, string all, FilterItem? chosen)
    {
        var wanted = new List<FilterItem> { new(null, all) };
        wanted.AddRange(values.Where(v => v.Length > 0).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).Select(v => new FilterItem(v, v)));
        if (!items.SequenceEqual(wanted))
        {
            items.Clear();
            foreach (var item in wanted)
                items.Add(item);
        }
        return items.FirstOrDefault(i => i.Value == chosen?.Value) ?? items[0];
    }

    [RelayCommand]
    private void Refresh()
    {
        if (Current is { } repo)
            Service.PollSoon(repo);
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (Current is not { } repo)
            return;
        IsLoadingMore = true;
        try
        {
            await Service.LoadMoreAsync(repo);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    public async Task RerunFailedAsync(WorkflowRun run)
    {
        if ((Current ?? Service.Repository(Service.ActiveFolder)) is not { } repo)
            return;
        var ask = new ConfirmDialogViewModel(L.T("Re-run Failed Jobs"),
            L.T($"Run the failed jobs of {run.Workflow} #{run.Number} on {run.Branch} again (and the jobs that depend on them)?"),
            L.T("Re-run"), destructive: false);
        if (await _dialogs.ShowDialogAsync(ask))
            await Service.RerunFailedAsync(repo, run);
    }

    public async Task RerunJobAsync(CiRepository repo, WorkflowJob job)
    {
        var ask = new ConfirmDialogViewModel(L.T("Re-run Job"), L.T($"Run \"{job.Name}\" again?"), L.T("Re-run"), destructive: false);
        if (await _dialogs.ShowDialogAsync(ask))
            await Service.RerunJobAsync(repo, job);
    }

    /// <summary>The job's log in a tab of its own.</summary>
    public void OpenLog(CiRepository repo, WorkflowRun run, WorkflowJob job) =>
        ((IDocumentService?)_provider.GetService(typeof(IDocumentService)))?.OpenById(CiLogDocumentFactory.IdFor(repo.Folder, run.Id, job.Id, job.Name));

    public void Dispose()
    {
        Service.RunsChanged -= OnRunsChanged;
        Service.ActiveChanged -= OnActiveChanged;
    }
}
