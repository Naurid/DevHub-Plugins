using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Board.Git.Core;
using DevHub.Git.Core;
using DevHub.Integration;
using DevHub.Services.Board;
using DevHub.Services.BoardGit;
using DevHub.ViewModels.Dialogs;
using DevHub.ViewModels.Documents;
using DevHub.Sdk.Core.Localization;

namespace DevHub.ViewModels.BoardGit;

/// <summary>The Development section of every issue (Board.Git).</summary>
public sealed class DevelopmentSectionProvider(BoardGitServices services, IServiceProvider provider) : IIssueSectionProvider
{
    public int Order => 400;

    public IssueSectionViewModel Create(BoardContext context, string issueId) =>
        new DevelopmentSectionViewModel(context, issueId, services, () => (IDocumentService?)provider.GetService(typeof(IDocumentService)));
}

/// <summary>A branch row: name, local or remote.</summary>
public sealed record BranchRow(string Name, bool IsRemote)
{
    public string Kind => IsRemote ? "remote" : "local";
    public override string ToString() => Name;
}

/// <summary>A commit row.</summary>
public sealed record CommitRow(string ShortSha, string Subject, string Author, string When)
{
    public override string ToString() => $"{ShortSha} {Subject}";
}

/// <summary>A pull request row (GitHub).</summary>
public sealed record PullRequestRow(int Number, string Title, string State, string Url)
{
    public override string ToString() => $"#{Number} {Title} ({State})";
}

/// <summary>
/// An issue's development: the branches whose name holds its key, the commits whose message does, and pull requests
/// on GitHub (when the GitHub plugin is on and the repository is there). Create Branch starts one.
/// </summary>
public sealed partial class DevelopmentSectionViewModel : IssueSectionViewModel
{
    private readonly BoardGitServices _services;
    private readonly Func<IDocumentService?> _documents;
    private CancellationTokenSource? _loading;
    private string? _repository;
    private string _key = "";

    public DevelopmentSectionViewModel(BoardContext context, string issueId, BoardGitServices services, Func<IDocumentService?> documents)
        : base(context, issueId)
    {
        _services = services;
        _documents = documents;
    }

    public override string Title => L.T("Development");

    public ObservableCollection<BranchRow> Branches { get; } = [];
    public ObservableCollection<CommitRow> Commits { get; } = [];
    public ObservableCollection<PullRequestRow> PullRequests { get; } = [];

    [ObservableProperty] private string? _repositoryText;
    [ObservableProperty] private bool _hasRepository;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _summary;
    public bool HasPullRequests => PullRequests.Count > 0;

    public override void Update(Issue issue, BoardSnapshot board)
    {
        if (issue.Key == _key && _repository is not null)
            return;
        _key = issue.Key;
        _ = LoadAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            _repository = await _services.RepositoryForAsync(Context.Location, cts.Token);
            HasRepository = _repository is not null;
            RepositoryText = _repository is null ? L.T("No repository: this board's folder isn't in one. Link one to see branches and commits.") : _repository;
            Branches.Clear();
            Commits.Clear();
            PullRequests.Clear();
            OnPropertyChanged(nameof(HasPullRequests));
            if (_repository is null)
            {
                Summary = null;
                return;
            }
            var info = await _services.Queries.ForIssueAsync(_repository, Context.Project.Key, _key, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            foreach (var branch in info.Branches)
                Branches.Add(new BranchRow(branch.Name, branch.IsRemote));
            foreach (var commit in info.Commits.Take(20))
                Commits.Add(new CommitRow(commit.ShortSha, commit.Subject, commit.Author, DevHub.ViewModels.Board.IssueDetailViewModel.RelativeOrDate(commit.Date)));
            Summary = info.Branches.Count == 0 && info.Commits.Count == 0
                ? L.T($"No branch or commit mentions {_key} yet.")
                : L.T($"{info.Branches.Count} branch(es), {info.Commits.Count} commit(s)");
            await LoadPullRequestsAsync(info.Branches, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (GitException e)
        {
            Summary = L.T("git couldn't read the repository: ") + e.StandardError.Trim();
        }
        finally
        {
            if (ReferenceEquals(_loading, cts))
                IsLoading = false;
        }
    }

    // Pull requests whose branch names the issue, on the GitHub repository behind "origin" (best effort, quiet on errors).
    private async Task LoadPullRequestsAsync(IReadOnlyList<IssueBranch> branches, CancellationToken ct)
    {
        if (_services.GitHub is not { } api || _repository is null)
            return;
        var url = (await _services.Runner.RunAsync(_repository, ["remote", "get-url", "origin"], new GitRunOptions { ReadOnly = true, AllowFailure = true }, ct))
            .StandardOutput.Trim();
        if (GitHubRepository(url) is not { } repo)
            return;
        try
        {
            if ((await api.GetAccountsAsync(ct)).Count == 0)
                return;
            var response = await api.SendAsync(GitHubApiRequest.Get($"repos/{repo}/pulls?state=all&per_page=100"), ct);
            if (!response.IsSuccess)
                return;
            using var json = JsonDocument.Parse(response.Body);
            foreach (var pr in json.RootElement.EnumerateArray())
            {
                var head = pr.GetProperty("head").GetProperty("ref").GetString() ?? "";
                var title = pr.GetProperty("title").GetString() ?? "";
                if (!IssueKeys.Find(head + " " + title, Context.Project.Key).Contains(_key, StringComparer.OrdinalIgnoreCase))
                    continue;
                var state = pr.TryGetProperty("merged_at", out var merged) && merged.ValueKind == JsonValueKind.String ? "merged" : pr.GetProperty("state").GetString() ?? "";
                PullRequests.Add(new PullRequestRow(pr.GetProperty("number").GetInt32(), title, state, pr.GetProperty("html_url").GetString() ?? ""));
            }
            OnPropertyChanged(nameof(HasPullRequests));
        }
        catch (Exception e) when (e is GitHubApiUnavailableException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }
    }

    /// <summary>"owner/name" of a github.com remote URL (https or ssh), or null.</summary>
    public static string? GitHubRepository(string url)
    {
        var u = url.Trim();
        foreach (var prefix in new[] { "https://github.com/", "http://github.com/", "git@github.com:", "ssh://git@github.com/" })
        {
            if (!u.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var path = u[prefix.Length..].TrimEnd('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                path = path[..^4];
            return path.Count(c => c == '/') == 1 ? path : null;
        }
        return null;
    }

    [RelayCommand]
    private async Task CreateBranchAsync()
    {
        if (Context.Snapshot.Issue(IssueId) is { } issue)
            await BranchCommands.CreateBranchAsync(Context, issue, _services);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LinkRepositoryAsync()
    {
        var folder = await _services.Dialogs.PickFolderAsync(L.T("Repository this board's issues are developed in"));
        if (folder is null)
            return;
        var top = await _services.Queries.RepositoryOfAsync(folder);
        if (top is null)
        {
            _services.Status.Show(L.T($"{folder} isn't a git repository."));
            return;
        }
        _services.LinkRepository(Context.Location, top);
        await LoadAsync();
    }

    [RelayCommand]
    private void OpenRepository()
    {
        if (_repository is not null)
            _documents()?.OpenById(RepositoryDocumentFactory.IdFor(_repository));
    }

    [RelayCommand]
    private void OpenPullRequest(PullRequestRow? row)
    {
        if (row is null || !Uri.TryCreate(row.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _services.Status.Show(L.T("The browser couldn't be opened."));
        }
    }

    public override void Dispose()
    {
        _loading?.Cancel();
        base.Dispose();
    }
}

/// <summary>Create Branch from an issue: the name (feature/DEV-12-summary), check it out, and maybe start the issue.</summary>
public sealed partial class CreateBranchDialogViewModel : DialogViewModelBase
{
    private readonly GitRepositoryClient _client;

    public CreateBranchDialogViewModel(GitRepositoryClient client, string suggestedName, string? baseBranch, bool canStart, string startTo)
    {
        _client = client;
        _name = suggestedName;
        BaseText = baseBranch is null ? L.T("From the current commit (detached HEAD).") : L.T($"From {baseBranch}.");
        CanStart = canStart;
        StartText = L.T($"Move the issue to {startTo}");
        _startIssue = canStart;
    }

    public override string Title => L.T("Create Branch");
    public override string OkText => L.T("Create");
    public override bool FitsContent => true;
    public override double Width => 520;
    public override double MinHeight => 0;

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _checkout = true;
    [ObservableProperty] private bool _startIssue;
    [ObservableProperty] private string? _error;
    public string BaseText { get; }
    public bool CanStart { get; }
    public string StartText { get; }

    protected override async Task<bool> ApplyChangesAsync()
    {
        Name = Name.Trim();
        if (Name.Length == 0 || !await _client.IsValidBranchNameAsync(Name))
        {
            Error = L.T("git doesn't allow that branch name.");
            return false;
        }
        if ((await _client.GetBranchesAsync()).Any(b => !b.IsRemote && string.Equals(b.Name, Name, StringComparison.OrdinalIgnoreCase)))
        {
            Error = L.T($"A branch named {Name} already exists.");
            return false;
        }
        return true;
    }
}

/// <summary>Create Branch, shared by the issue action and the Development section.</summary>
public static class BranchCommands
{
    public static async Task CreateBranchAsync(BoardContext context, Issue issue, BoardGitServices services)
    {
        var repository = await services.RepositoryForAsync(context.Location);
        if (repository is null)
        {
            services.Status.Show(L.T("This board isn't linked to a repository: use Link Repository… in the issue's Development section."));
            return;
        }
        var client = new GitRepositoryClient(services.Runner, repository);
        var snapshot = context.Snapshot;
        var workflow = snapshot.WorkflowFor(issue.Type);
        var start = snapshot.CategoryOf(issue) == StatusCategory.ToDo
            ? workflow?.Statuses.Select(snapshot.Project.Status).OfType<Status>()
                .FirstOrDefault(s => s.Category == StatusCategory.InProgress && workflow.Allows(issue.Status, s.Id))
            : null;
        var dialog = new CreateBranchDialogViewModel(client, BranchNames.For(issue, snapshot.Project),
            await services.CurrentBranchAsync(repository), start is not null, start?.Name ?? "");
        if (!await services.Dialogs.ShowDialogAsync(dialog))
            return;
        try
        {
            await client.CreateBranchAsync(dialog.Name, startPoint: null, checkout: dialog.Checkout);
            services.Status.Show(dialog.Checkout ? L.T($"Created and switched to {dialog.Name}") : L.T($"Created {dialog.Name}"));
        }
        catch (GitException e)
        {
            await services.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel(L.T("Create Branch"), L.T("git couldn't create the branch."), "OK",
                [e.StandardError.Trim()], destructive: false, showCancel: false));
            return;
        }
        if (dialog.StartIssue && start is not null)
            await context.RunAsync(new TransitionIssue(issue.Id, start.Id));
    }
}

/// <summary>Create Branch in the issue view's header and the card menu.</summary>
public sealed class CreateBranchActionProvider(BoardGitServices services) : IIssueActionProvider
{
    public IEnumerable<IssueAction> GetActions(BoardContext context, Issue issue)
    {
        if (context.Snapshot.LevelOf(issue) == IssueTypeLevel.Epic)
            yield break;
        yield return new IssueAction(L.T("Create Branch…"), () => BranchCommands.CreateBranchAsync(context, issue, services));
    }
}
