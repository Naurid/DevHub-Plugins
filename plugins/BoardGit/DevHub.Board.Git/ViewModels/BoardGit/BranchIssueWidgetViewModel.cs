using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board.Core;
using DevHub.Board.Git.Core;
using DevHub.Git.Core;
using DevHub.Integration;
using DevHub.Services.Board;
using DevHub.Services.BoardGit;
using DevHub.ViewModels.Dialogs;
using DevHub.ViewModels.Documents;
using DevHub.ViewModels.Shell;
using DevHub.Sdk.Core.Localization;

namespace DevHub.ViewModels.BoardGit;

/// <summary>
/// The status bar's issue: on a branch named after an issue (feature/DEV-12-…) of a repository tab in front, it shows
/// "DEV-12 Login form"; a click opens the issue. It also watches commits made from DevHub for smart commit commands
/// (DEV-12 #done #comment … #time 2h) and asks before applying them, never silently.
/// </summary>
public sealed partial class BranchIssueWidgetViewModel : StatusBarWidgetViewModelBase, IDisposable
{
    private readonly BoardGitServices _services;
    private readonly IServiceProvider _provider;
    private IDocumentService? _documents;
    private IRepositoryEvents? _events;
    private RecentBoard? _board;
    private string? _issueId;
    private int _refresh;

    public BranchIssueWidgetViewModel(BoardGitServices services, IServiceProvider provider)
    {
        _services = services;
        _provider = provider;
        // Subscribed once startup is done: the document service and Git's events are still being built while the
        // status bar is (taking them here hangs startup, docs/UI_GUIDE.md §8).
        Avalonia.Threading.Dispatcher.UIThread.Post(Attach, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void Attach()
    {
        _documents = (IDocumentService?)_provider.GetService(typeof(IDocumentService));
        _events = (IRepositoryEvents?)_provider.GetService(typeof(IRepositoryEvents));
        if (_documents is not null)
            _documents.PropertyChanged += OnDocumentsChanged;
        if (_events is not null)
        {
            _events.BranchChanged += OnBranchChanged;
            _events.Committed += OnCommitted;
        }
        _ = RefreshAsync();
    }

    public override int Order => 50;

    [ObservableProperty] private string? _text;
    [ObservableProperty] private string? _toolTip;
    public bool IsVisible => Text is not null;

    partial void OnTextChanged(string? value) => OnPropertyChanged(nameof(IsVisible));

    private void OnDocumentsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IDocumentService.Active))
            _ = RefreshAsync();
    }

    private void OnBranchChanged(object? sender, RepositoryEventArgs e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        var token = ++_refresh;
        var folder = _documents?.Active?.ContextPath;
        string? text = null, tip = null;
        RecentBoard? board = null;
        string? issueId = null;
        if (folder is not null && Directory.Exists(folder))
        {
            try
            {
                var repository = await _services.Queries.RepositoryOfAsync(folder);
                var branch = repository is null ? null : await _services.CurrentBranchAsync(repository);
                if (repository is not null && branch is not null)
                {
                    foreach (var candidate in await _services.BoardsOfRepositoryAsync(repository))
                    {
                        if (IssueKeys.Find(branch, candidate.Key).FirstOrDefault() is not { } key)
                            continue;
                        var found = await _services.WithBoardAsync(candidate, c =>
                            Task.FromResult(c.Snapshot.IssueByKey(key) is { } i ? (i.Id, $"{i.Key} {i.Summary}", L.T($"{i.Key} · {c.StatusName(i.Status)} on {candidate.Name}: open the issue")) : default));
                        if (found.Id is null)
                            continue;
                        (issueId, text, tip) = found;
                        board = candidate;
                        break;
                    }
                }
            }
            catch (GitException)
            {
            }
        }
        if (token != _refresh)
            return;
        _board = board;
        _issueId = issueId;
        Text = text is null ? null : text.Length > 48 ? text[..48] + "…" : text;
        ToolTip = tip;
    }

    [RelayCommand]
    private void Open()
    {
        if (_board is not null && _issueId is not null)
            _documents?.OpenById(IssueDocumentFactory.IdFor(new BoardLocation(_board.StoreKind, _board.Location), _issueId));
    }

    // Smart commits: commands in the message of a commit made from DevHub, confirmed before they apply.
    private async void OnCommitted(object? sender, RepositoryEventArgs e)
    {
        try
        {
            if (!_services.Settings.SmartCommits || e.CommitSha is null)
                return;
            var message = await new GitRepositoryClient(_services.Runner, e.RepositoryFolder).GetCommitMessageAsync(e.CommitSha);
            foreach (var board in await _services.BoardsOfRepositoryAsync(e.RepositoryFolder))
            {
                await _services.WithBoardAsync(board, async context =>
                {
                    var commands = SmartCommits.Parse(message, context.Snapshot);
                    if (commands.Count == 0)
                        return false;
                    var settings = context.Project.Settings;
                    var ok = await _services.Dialogs.ShowDialogAsync(new ConfirmDialogViewModel(L.T("Smart Commit"),
                        L.T($"The commit message asks for these changes on {board.Name}. Apply them?"), L.T("Apply"),
                        commands.Select(c => c.Describe(settings)).ToList(), destructive: false));
                    if (!ok)
                        return false;
                    foreach (var command in commands)
                        await context.RunAsync(command.ToOperation(DateTimeOffset.Now));
                    return true;
                });
            }
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is GitException or IOException or InvalidOperationException)
        {
            _services.Status.Show(L.T("Smart commit skipped: ") + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_documents is not null)
            _documents.PropertyChanged -= OnDocumentsChanged;
        if (_events is not null)
        {
            _events.BranchChanged -= OnBranchChanged;
            _events.Committed -= OnCommitted;
        }
    }
}
