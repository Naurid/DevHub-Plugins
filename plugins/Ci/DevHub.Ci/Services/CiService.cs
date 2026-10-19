using System.ComponentModel;
using System.Net;
using Avalonia.Threading;
using DevHub.Ci.Core;
using DevHub.Integration;
using DevHub.Sdk.Core.Localization;
using DevHub.Services;
using DevHub.ViewModels.Documents;

namespace DevHub.Services.Ci;

/// <summary>What the CI tracker knows about one repository (an open repository tab with a GitHub origin).</summary>
public sealed class CiRepository(string folder, string slug, DateTimeOffset since)
{
    public string Folder { get; } = folder;

    /// <summary>"owner/name" on GitHub.</summary>
    public string Slug { get; } = slug;

    /// <summary>Newest first: the first page as last polled, then the pages loaded with Load more.</summary>
    public IReadOnlyList<WorkflowRun> Runs { get; internal set; } = [];

    public int TotalCount { get; internal set; }
    public bool HasMore => NextPage is not null;
    public bool Loaded { get; internal set; }

    /// <summary>Why the runs can't be shown (not signed in, no access to Actions), or null.</summary>
    public string? Problem { get; internal set; }

    internal string? ETag;
    internal string? NextPage;
    internal DateTimeOffset NextPoll;
    internal DateTimeOffset? LastPush;
    internal readonly RunWatch Watch = new(since);
    internal readonly Dictionary<long, (string? ETag, long RunUpdated, IReadOnlyList<WorkflowJob> Jobs)> Jobs = new();
}

/// <summary>
/// The CI tracker: the GitHub Actions runs of the repositories open as tabs, polled in one background loop at a pace
/// that follows what's going on (<see cref="PollSchedule"/>), with ETags so a quiet repository costs no rate limit.
/// Says in the status bar when a watched run finishes, marks commits and branches in Git (<see cref="IGitDecorator"/>),
/// and loads jobs and logs only when they're asked for.
/// </summary>
public sealed class CiService : IGitDecorator, IDisposable
{
    private readonly IGitHubApi _api;
    private readonly IStatusService _status;
    private readonly IServiceProvider _provider;
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private readonly Dictionary<string, CiRepository> _repositories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private IDocumentService? _documents;
    private IRepositoryEvents? _events;
    private int? _rateLimitRemaining;
    private bool _attached;

    public CiService(IGitHubApi api, IStatusService status, IServiceProvider provider)
    {
        _api = api;
        _status = status;
        _provider = provider;
        // The document service and Git's events are still being built while plugins' services are: looked up once
        // startup is done (taking them here would be a DI cycle through the log tabs' factory).
        Dispatcher.UIThread.Post(Attach, DispatcherPriority.Background);
        _ = Task.Run(LoopAsync);
    }

    /// <summary>A repository's runs changed (raised on the UI thread with its folder).</summary>
    public event EventHandler<string>? RunsChanged;

    /// <summary>Another tab came to the front, or its branch changed (UI thread).</summary>
    public event EventHandler? ActiveChanged;

    public event EventHandler<DecorationsChangedEventArgs>? Changed;

    private void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        _documents = (IDocumentService?)_provider.GetService(typeof(IDocumentService));
        _events = (IRepositoryEvents?)_provider.GetService(typeof(IRepositoryEvents));
        if (_documents is not null)
        {
            _documents.OpenDocumentsChanged += OnDocumentsChanged;
            _documents.PropertyChanged += OnActiveChanged;
        }
        if (_events is not null)
        {
            _events.Pushed += OnPushed;
            _events.BranchChanged += OnBranchChanged;
        }
        UpdateWatched();
    }

    /// <summary>What's known about the repository at <paramref name="folder"/>, or null when it isn't watched (no tab, not GitHub).</summary>
    public CiRepository? Repository(string? folder)
    {
        if (folder is null)
            return null;
        lock (_gate)
            return _repositories.GetValueOrDefault(folder);
    }

    /// <summary>The repository tab in front, if it's a watched one.</summary>
    public string? ActiveFolder => _documents?.Active?.ContextPath;

    // ---- What is watched ---------------------------------------------------------------------------------------

    private void OnDocumentsChanged(object? sender, EventArgs e) => UpdateWatched();

    // The tab in front is polled straight away when it hasn't been yet (it may have opened before DevHub knew).
    private void OnActiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IDocumentService.Active))
            return;
        if (Repository(ActiveFolder) is { Loaded: false } repo)
            PollSoon(repo);
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnBranchChanged(object? sender, RepositoryEventArgs e) => ActiveChanged?.Invoke(this, EventArgs.Empty);

    private void UpdateWatched()
    {
        if (_documents is null)
            return;
        var folders = _documents.OpenDocuments.Select(d => d.ContextPath).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
        lock (_gate)
        {
            foreach (var gone in _repositories.Keys.Where(k => !folders.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
                _repositories.Remove(gone);
            foreach (var folder in folders.Where(f => !_repositories.ContainsKey(f)))
                if (GitHubRemote.Of(folder) is { } slug)
                    _repositories[folder] = new CiRepository(folder, slug, _started) { NextPoll = DateTimeOffset.Now };
        }
    }

    private void OnPushed(object? sender, PushEventArgs e)
    {
        if (Repository(e.RepositoryFolder) is { } repo)
        {
            repo.LastPush = DateTimeOffset.Now;
            PollSoon(repo);
        }
    }

    /// <summary>Asks GitHub again now (Refresh, after a re-run).</summary>
    public void PollSoon(CiRepository repo) => repo.NextPoll = DateTimeOffset.Now;

    // ---- The loop ----------------------------------------------------------------------------------------------

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                List<CiRepository> due;
                lock (_gate)
                    due = _repositories.Values.Where(r => r.NextPoll <= DateTimeOffset.Now).ToList();
                foreach (var repo in due)
                    await PollAsync(repo, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // DevHub is closing.
        }
    }

    private async Task PollAsync(CiRepository repo, CancellationToken ct)
    {
        repo.NextPoll = DateTimeOffset.Now + PollSchedule.Idle;   // whatever happens, not again at once
        try
        {
            var response = await _api.SendAsync(GitHubApiRequest.Get($"repos/{repo.Slug}/actions/runs?per_page=20", repo.ETag), ct);
            _rateLimitRemaining = response.RateLimitRemaining ?? _rateLimitRemaining;
            if (response.NotModified)
            {
                Schedule(repo);
                return;
            }
            if (!response.IsSuccess)
            {
                repo.Problem = response.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
                    ? L.T($"GitHub doesn't show this account the Actions of {repo.Slug}.")
                    : L.T($"GitHub answered {(int)response.Status} for {repo.Slug}'s runs.");
                repo.Loaded = true;
                Raise(repo, []);
                Schedule(repo);
                return;
            }
            var page = ActionsJson.ParseRuns(response.Body);
            var finished = repo.Watch.Update(page.Runs);
            lock (_gate)
            {
                // The new first page, then the older runs loaded before (Load more) that it doesn't cover.
                var oldest = page.Runs.Count > 0 ? page.Runs.Min(r => r.Created) : DateTimeOffset.MaxValue;
                var ids = page.Runs.Select(r => r.Id).ToHashSet();
                repo.Runs = [.. page.Runs, .. repo.Runs.Where(r => !ids.Contains(r.Id) && r.Created < oldest)];
                repo.TotalCount = page.TotalCount;
                repo.ETag = response.ETag;
                if (!repo.Loaded || repo.NextPage is null)
                    repo.NextPage = response.NextPageUrl;
                repo.Loaded = true;
                repo.Problem = null;
            }
            Raise(repo, finished);
            Schedule(repo);
        }
        catch (GitHubApiUnavailableException e)
        {
            repo.Problem = e.Message;
            repo.Loaded = true;
            Raise(repo, []);
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // No network for a moment, or an answer that didn't parse: try again at the idle pace.
        }
    }

    private void Schedule(CiRepository repo)
    {
        var active = repo.Runs.Any(r => r.State.IsActive());
        repo.NextPoll = DateTimeOffset.Now + PollSchedule.Next(active, repo.LastPush, DateTimeOffset.Now, _rateLimitRemaining);
    }

    // Tell the UI (and Git's marks); say in the status bar what just finished.
    private void Raise(CiRepository repo, IReadOnlyList<RunFinished> finished) =>
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var f in finished)
                _status.Show(Describe(f.Run, repo));
            RunsChanged?.Invoke(this, repo.Folder);
            Changed?.Invoke(this, new DecorationsChangedEventArgs(repo.Folder));
        });

    private static string Describe(WorkflowRun run, CiRepository repo)
    {
        var name = Path.GetFileName(repo.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return run.State switch
        {
            RunState.Passed => L.T($"✓ {run.Workflow} passed on {run.Branch} · {name}"),
            RunState.Failed => L.T($"✗ {run.Workflow} failed on {run.Branch} · {name}"),
            RunState.Cancelled => L.T($"⊘ {run.Workflow} was cancelled on {run.Branch} · {name}"),
            _ => L.T($"{run.Workflow} finished on {run.Branch} · {name}"),
        };
    }

    // ---- On demand: more runs, jobs, logs, re-runs -------------------------------------------------------------

    /// <summary>The next page of older runs.</summary>
    public async Task LoadMoreAsync(CiRepository repo)
    {
        if (repo.NextPage is not { } next)
            return;
        try
        {
            var response = await _api.SendAsync(GitHubApiRequest.Get(next));
            if (!response.IsSuccess)
                return;
            var page = ActionsJson.ParseRuns(response.Body);
            lock (_gate)
            {
                var ids = repo.Runs.Select(r => r.Id).ToHashSet();
                repo.Runs = [.. repo.Runs, .. page.Runs.Where(r => !ids.Contains(r.Id))];
                repo.NextPage = response.NextPageUrl;
            }
            RunsChanged?.Invoke(this, repo.Folder);
        }
        catch (Exception e) when (e is GitHubApiUnavailableException or HttpRequestException or System.Text.Json.JsonException)
        {
            _status.Show(L.T("Couldn't load more runs: ") + e.Message);
        }
    }

    /// <summary>A run's jobs (kept until the run changes; asked with the last ETag).</summary>
    public async Task<IReadOnlyList<WorkflowJob>> JobsAsync(CiRepository repo, WorkflowRun run)
    {
        var updated = run.Updated.ToUnixTimeSeconds();
        (string? ETag, long RunUpdated, IReadOnlyList<WorkflowJob> Jobs) cached;
        lock (_gate)
            repo.Jobs.TryGetValue(run.Id, out cached);
        if (cached.Jobs is not null && cached.RunUpdated == updated && !run.State.IsActive())
            return cached.Jobs;
        var response = await _api.SendAsync(GitHubApiRequest.Get($"repos/{repo.Slug}/actions/runs/{run.Id}/jobs?per_page=100", cached.ETag));
        if (response.NotModified && cached.Jobs is not null)
            return cached.Jobs;
        if (!response.IsSuccess)
            throw new GitHubApiUnavailableException(L.T($"GitHub answered {(int)response.Status} for the jobs of run #{run.Number}."));
        var jobs = ActionsJson.ParseJobs(response.Body);
        lock (_gate)
            repo.Jobs[run.Id] = (response.ETag, updated, jobs);
        return jobs;
    }

    /// <summary>
    /// A job's whole log as GitHub stores it. Kept once the job is done (it doesn't change any more), by job id, which
    /// is unique on GitHub: a log tab restored at startup finds it without knowing the run.
    /// </summary>
    public async Task<string> LogAsync(string slug, long jobId)
    {
        lock (_gate)
            if (_logs.TryGetValue(jobId, out var kept))
                return kept;
        // GitHub answers with a redirect to a signed download address; the HTTP client follows it without the token.
        var response = await _api.SendAsync(GitHubApiRequest.Get($"repos/{slug}/actions/jobs/{jobId}/logs"));
        if (!response.IsSuccess)
            throw new GitHubApiUnavailableException(response.Status == HttpStatusCode.Gone
                ? L.T("GitHub no longer keeps this log (logs expire after a while).")
                : L.T($"GitHub answered {(int)response.Status} for this job's log."));
        if (IsDone(slug, jobId))
            lock (_gate)
                _logs[jobId] = response.Body;
        return response.Body;
    }

    private readonly Dictionary<long, string> _logs = new();

    // Whether a job is known to be finished (its run's jobs were loaded); unknown: not kept, asked again next time.
    private bool IsDone(string slug, long jobId)
    {
        lock (_gate)
            return _repositories.Values.Where(r => r.Slug == slug).SelectMany(r => r.Jobs.Values)
                .SelectMany(j => j.Jobs).Any(j => j.Id == jobId && !j.State.IsActive());
    }

    /// <summary>Opens a GitHub page in the browser (only https://github.com addresses: they come from the API).</summary>
    public static void OpenInBrowser(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    /// <summary>Runs one job again. True when GitHub took it.</summary>
    public Task<bool> RerunJobAsync(CiRepository repo, WorkflowJob job) =>
        PostAsync(repo, $"repos/{repo.Slug}/actions/jobs/{job.Id}/rerun", L.T($"Re-running {job.Name}…"));

    /// <summary>Runs a run's failed jobs again (and the jobs that depend on them).</summary>
    public Task<bool> RerunFailedAsync(CiRepository repo, WorkflowRun run) =>
        PostAsync(repo, $"repos/{repo.Slug}/actions/runs/{run.Id}/rerun-failed-jobs", L.T($"Re-running the failed jobs of {run.Workflow} #{run.Number}…"));

    private async Task<bool> PostAsync(CiRepository repo, string path, string started)
    {
        try
        {
            var response = await _api.SendAsync(new GitHubApiRequest("POST", path));
            if (response.IsSuccess)
            {
                _status.Show(started);
                repo.LastPush = DateTimeOffset.Now;   // the new attempt shows up in a few seconds: look often
                PollSoon(repo);
                return true;
            }
            _status.Show(response.Status == HttpStatusCode.Forbidden
                ? L.T("GitHub refused: re-running needs the account's \"repo\" and \"workflow\" rights (Settings → GitHub → Sign In again).")
                : L.T($"GitHub refused the re-run ({(int)response.Status})."));
        }
        catch (GitHubApiUnavailableException e)
        {
            _status.Show(e.Message);
        }
        return false;
    }

    // ---- Marks in Git ------------------------------------------------------------------------------------------

    public IReadOnlyDictionary<string, IReadOnlyList<Decoration>> DecorateCommits(string repositoryFolder, IReadOnlyList<string> commitShas)
    {
        var result = new Dictionary<string, IReadOnlyList<Decoration>>(StringComparer.Ordinal);
        if (Repository(repositoryFolder) is not { } repo)
            return result;
        var wanted = commitShas.ToHashSet(StringComparer.Ordinal);
        foreach (var group in Latest(repo.Runs).GroupBy(r => r.HeadSha).Where(g => wanted.Contains(g.Key)))
            result[group.Key] = [Mark(group.ToList())];
        return result;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<Decoration>> DecorateBranches(string repositoryFolder, IReadOnlyList<string> branches)
    {
        var result = new Dictionary<string, IReadOnlyList<Decoration>>(StringComparer.Ordinal);
        if (Repository(repositoryFolder) is not { } repo)
            return result;
        foreach (var branch in branches)
        {
            var name = branch.StartsWith("origin/", StringComparison.Ordinal) ? branch["origin/".Length..] : branch;
            var runs = LatestOf(repo.Runs, name);
            if (runs.Count > 0)
                result[branch] = [Mark(runs)];
        }
        return result;
    }

    /// <summary>
    /// What "the branch's CI" means: the runs of the newest commit that ran on <paramref name="branch"/>, the latest
    /// attempt of each workflow.
    /// </summary>
    public static IReadOnlyList<WorkflowRun> LatestOf(IEnumerable<WorkflowRun> runs, string branch)
    {
        var onBranch = runs.Where(r => r.Branch == branch).ToList();
        if (onBranch.MaxBy(r => r.Created)?.HeadSha is not { } newest)
            return [];
        return Latest(onBranch.Where(r => r.HeadSha == newest)).ToList();
    }

    // The newest attempt of each run (a re-run replaces the result), per workflow and commit.
    private static IEnumerable<WorkflowRun> Latest(IEnumerable<WorkflowRun> runs) =>
        runs.GroupBy(r => (r.Workflow, r.HeadSha)).Select(g => g.MaxBy(r => r.Created)!);

    /// <summary>One mark for several runs: failed if any failed, running if any runs, passed if all passed.</summary>
    public static Decoration Mark(IReadOnlyList<WorkflowRun> runs)
    {
        var tip = string.Join("\n", runs.Select(r => $"{r.Workflow} #{r.Number}: {StateText(r.State)}"));
        var url = (runs.FirstOrDefault(r => r.State == RunState.Failed) ?? runs[0]).HtmlUrl;
        return Combined(runs) switch
        {
            RunState.Failed => new Decoration(Controls.IconKind.Error, DecorationTone.Error, tip, url),
            RunState.Running or RunState.Queued => new Decoration(Controls.IconKind.Run, DecorationTone.Running, tip, url),
            RunState.Passed => new Decoration(Controls.IconKind.Success, DecorationTone.Success, tip, url),
            _ => new Decoration(Controls.IconKind.Warning, DecorationTone.Neutral, tip, url),
        };
    }

    public static RunState Combined(IReadOnlyList<WorkflowRun> runs) =>
        runs.Any(r => r.State == RunState.Failed) ? RunState.Failed
        : runs.Any(r => r.State.IsActive()) ? RunState.Running
        : runs.Count > 0 && runs.All(r => r.State is RunState.Passed or RunState.Skipped) ? RunState.Passed
        : RunState.Other;

    public static string StateText(RunState state) => state switch
    {
        RunState.Queued => L.T("queued"),
        RunState.Running => L.T("running"),
        RunState.Passed => L.T("passed"),
        RunState.Failed => L.T("failed"),
        RunState.Cancelled => L.T("cancelled"),
        RunState.Skipped => L.T("skipped"),
        _ => L.T("finished"),
    };

    public void Dispose()
    {
        _stop.Cancel();
        if (_documents is not null)
        {
            _documents.OpenDocumentsChanged -= OnDocumentsChanged;
            _documents.PropertyChanged -= OnActiveChanged;
        }
        if (_events is not null)
        {
            _events.Pushed -= OnPushed;
            _events.BranchChanged -= OnBranchChanged;
        }
    }
}
