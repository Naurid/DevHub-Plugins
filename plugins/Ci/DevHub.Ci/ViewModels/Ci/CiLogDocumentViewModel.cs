using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Ci.Core;
using DevHub.Integration;
using DevHub.Sdk.Core.Localization;
using DevHub.Services.Ci;
using DevHub.ViewModels.Documents;

namespace DevHub.ViewModels.Ci;

/// <summary>
/// A job's log in a tab: by default only the failed steps (errors highlighted, timestamps gone), or all of it; Reload
/// and Open in Browser. One tab per job (<see cref="CiLogDocumentFactory"/>), restored at startup.
/// </summary>
public sealed partial class CiLogDocumentViewModel : DocumentViewModelBase
{
    private readonly CiService _service;
    private readonly string _folder;
    private readonly string? _slug;
    private readonly long _runId;
    private readonly long _jobId;
    private string _raw = "";

    public CiLogDocumentViewModel(CiService service, string folder, long runId, long jobId, string jobName)
    {
        _service = service;
        _folder = folder;
        _slug = GitHubRemote.Of(folder);
        _runId = runId;
        _jobId = jobId;
        JobName = jobName;
        _ = LoadAsync();
    }

    public override string Id => CiLogDocumentFactory.IdFor(_folder, _runId, _jobId, JobName);
    public override string Title => L.T($"Log · {JobName}");
    public override string Icon => Controls.IconKind.Terminal;
    public override string? ContextPath => _folder;

    public string JobName { get; }
    public ObservableCollection<LogLine> Lines { get; } = [];

    /// <summary>All of the log rather than the failed steps.</summary>
    [ObservableProperty] private bool _showAll;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string? _summary;

    partial void OnShowAllChanged(bool value) => Show();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_slug is null)
        {
            Problem = L.T("This repository has no GitHub origin any more.");
            return;
        }
        IsLoading = true;
        try
        {
            _raw = await _service.LogAsync(_slug, _jobId);
            Problem = null;
            Show();
        }
        catch (Exception e) when (e is GitHubApiUnavailableException or HttpRequestException)
        {
            Problem = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Show()
    {
        var lines = ShowAll ? LogExcerpt.Lines(_raw) : LogExcerpt.Failed(_raw);
        Lines.Clear();
        foreach (var line in lines)
            Lines.Add(line);
        var errors = lines.Count(l => l.IsError);
        Summary = ShowAll
            ? L.T($"{lines.Count} lines")
            : errors > 0 ? L.T($"The failed steps: {lines.Count} lines, {errors} errors") : L.T($"No error marked: the last {lines.Count} lines");
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        if (_slug is not null)
            CiService.OpenInBrowser($"https://github.com/{_slug}/actions/runs/{_runId}/job/{_jobId}");
    }
}

/// <summary>Log tabs, one per job: "CiLog:&lt;folder&gt;|&lt;run&gt;|&lt;job&gt;|&lt;job name&gt;".</summary>
public sealed class CiLogDocumentFactory(CiService service) : IDocumentFactory
{
    public const string Prefix = "CiLog:";

    public string IdPrefix => Prefix;

    public static string IdFor(string folder, long runId, long jobId, string jobName) => $"{Prefix}{folder}|{runId}|{jobId}|{jobName}";

    public DocumentViewModelBase? Create(string id)
    {
        var parts = id[Prefix.Length..].Split('|', 4);
        return parts.Length == 4 && Directory.Exists(parts[0]) && long.TryParse(parts[1], out var run) && long.TryParse(parts[2], out var job)
            ? new CiLogDocumentViewModel(service, parts[0], run, job, parts[3])
            : null;
    }
}
