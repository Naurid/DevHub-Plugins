using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Ci.Core;
using DevHub.Integration;
using DevHub.Sdk.Core.Formatting;
using DevHub.Sdk.Core.Localization;
using DevHub.Services.Ci;

namespace DevHub.ViewModels.Ci;

/// <summary>How a state looks: a symbol, and which colour class the view gives it.</summary>
public static class StateLook
{
    public static string Symbol(RunState state) => state switch
    {
        RunState.Passed => "✓",
        RunState.Failed => "✗",
        RunState.Running or RunState.Queued => "●",
        RunState.Cancelled => "⊘",
        _ => "–",
    };

    public static string Duration(TimeSpan? span) => span switch
    {
        null => "",
        { TotalHours: >= 1 } d => $"{(int)d.TotalHours}h {d.Minutes}m",
        { TotalMinutes: >= 1 } d => $"{(int)d.TotalMinutes}m {d.Seconds}s",
        { } d => $"{d.Seconds}s",
    };
}

/// <summary>A row of the CI tool window: one workflow run; expanded, its jobs.</summary>
public sealed partial class RunRowViewModel : ViewModelBase
{
    private readonly CiToolWindowViewModel _window;

    public RunRowViewModel(CiToolWindowViewModel window, WorkflowRun run)
    {
        _window = window;
        _run = run;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Symbol), nameof(Title), nameof(Meta), nameof(When), nameof(DurationText),
        nameof(IsPassed), nameof(IsFailed), nameof(IsActive), nameof(CanRerunFailed), nameof(StateText))]
    private WorkflowRun _run;

    public long Id => Run.Id;
    public string Symbol => StateLook.Symbol(Run.State);
    public string StateText => CiService.StateText(Run.State);
    public string Title => Run.Title.Length > 0 ? Run.Title : Run.Workflow;

    /// <summary>"CI #57 · main · push · naurid" (with the attempt when it was re-run).</summary>
    public string Meta => string.Join(" · ", new[]
    {
        $"{Run.Workflow} #{Run.Number}" + (Run.Attempt > 1 ? L.T($" (attempt {Run.Attempt})") : ""),
        Run.Branch, Run.Event, Run.Actor,
    }.Where(p => p.Length > 0));

    public string When => RelativeTime.Format(Run.Created, DateTimeOffset.Now);
    public string DurationText => StateLook.Duration(Run.Duration(DateTimeOffset.Now));
    public bool IsPassed => Run.State == RunState.Passed;
    public bool IsFailed => Run.State == RunState.Failed;
    public bool IsActive => Run.State.IsActive();
    public bool CanRerunFailed => Run.State is RunState.Failed or RunState.Cancelled;

    public ObservableCollection<JobRowViewModel> Jobs { get; } = [];

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoadingJobs;
    [ObservableProperty] private string? _jobsProblem;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            _ = LoadJobsAsync();
    }

    /// <summary>The run changed (a new poll): its jobs too, if they're shown.</summary>
    partial void OnRunChanged(WorkflowRun value)
    {
        if (IsExpanded)
            _ = LoadJobsAsync();
    }

    public async Task LoadJobsAsync()
    {
        if (_window.Current is not { } repo)
            return;
        IsLoadingJobs = Jobs.Count == 0;
        try
        {
            var jobs = await _window.Service.JobsAsync(repo, Run);
            Jobs.Clear();
            foreach (var job in jobs)
                Jobs.Add(new JobRowViewModel(_window, repo, Run, job));
            JobsProblem = null;
        }
        catch (Exception e) when (e is GitHubApiUnavailableException or HttpRequestException or System.Text.Json.JsonException)
        {
            JobsProblem = e.Message;
        }
        finally
        {
            IsLoadingJobs = false;
        }
    }

    [RelayCommand]
    private void OpenInBrowser() => CiService.OpenInBrowser(Run.HtmlUrl);

    [RelayCommand]
    private Task RerunFailedAsync() => _window.RerunFailedAsync(Run);

    public override string ToString() => $"{StateText}: {Meta}, {Title}";
}

/// <summary>A job of an expanded run: its state, its failed steps, Show Log and Re-run.</summary>
public sealed partial class JobRowViewModel(CiToolWindowViewModel window, CiRepository repo, WorkflowRun run, WorkflowJob job) : ViewModelBase
{
    public WorkflowJob Job { get; } = job;
    public string Symbol => StateLook.Symbol(Job.State);
    public string Name => Job.Name;
    public string DurationText => StateLook.Duration(Job.Duration(DateTimeOffset.Now));
    public bool IsPassed => Job.State == RunState.Passed;
    public bool IsFailed => Job.State == RunState.Failed;
    public bool IsActive => Job.State.IsActive();

    /// <summary>"Failed at: Test" (the steps that failed), or null.</summary>
    public string? FailedSteps => Job.FailedSteps.Count == 0 ? null
        : L.T("Failed at: ") + string.Join(", ", Job.FailedSteps.Select(s => s.Name));

    public bool HasLog => !Job.State.IsActive() && Job.State != RunState.Skipped;
    public bool CanRerun => !Job.State.IsActive() && !run.State.IsActive();

    [RelayCommand]
    private void ShowLog() => window.OpenLog(repo, run, Job);

    [RelayCommand]
    private Task RerunAsync() => window.RerunJobAsync(repo, Job);

    [RelayCommand]
    private void OpenInBrowser() => CiService.OpenInBrowser(Job.HtmlUrl);

    public override string ToString() => $"{Name}: {CiService.StateText(Job.State)}";
}

/// <summary>An item of the branch or workflow filter ("All branches" has a null value).</summary>
public sealed record FilterItem(string? Value, string Text)
{
    public override string ToString() => Text;
}

