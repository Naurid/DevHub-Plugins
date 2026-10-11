using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Sdk.Core.Formatting;
using DevHub.Git.Core;
using DevHub.Services;

namespace DevHub.ViewModels.Git;

/// <summary>
/// The All Commits section of a repository tab (Fork's "All Commits", JetBrains' Log): every
/// branch's history as a graph + list, newest first, and the selected commit's details below
/// (message, changed files, the selected file's diff). Right-click a commit: Cherry-Pick, Revert,
/// New Branch / New Tag here (G8).
/// </summary>
/// <remarks>
/// Loaded the first time the section is shown, then reloaded when a refresh sees the branches
/// or HEAD move (not on every file save: editing files doesn't change history). Loads
/// <see cref="PageSize"/> commits at a time; "Load more" fetches the next page.
/// </remarks>
public partial class CommitLogViewModel : ViewModelBase
{
    public const int PageSize = 1000;

    /// <summary>The graph never gets wider than this many lanes (wider repositories are clipped).</summary>
    private const int MaxGraphLanes = 16;

    private readonly GitRepositoryClient? _client;
    private readonly CommitOperations? _operations;
    private readonly IRemoteHost? _host;

    /// <summary>Your picture on the hosting service (GitHub), shared by every row you authored (they all update when it arrives).</summary>
    private readonly AvatarViewModel _myAvatar = new();

    /// <summary>How your commits are recognised: your e-mail(s) and names (git config, the host's profile).</summary>
    private readonly HashSet<string> _myEmails = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _myNames = new(StringComparer.OrdinalIgnoreCase);
    private Func<string, bool>? _isMyHostEmail;
    private CancellationTokenSource? _loadCts;
    private int _limit = PageSize;

    /// <summary>What the last load saw (branch tips + HEAD); null = never loaded.</summary>
    private string? _loadedSignature;

    private IReadOnlyList<CommitRowViewModel> _allRows = [];

    /// <param name="annotate">Opens a blame tab (file path, commit id).</param>
    /// <param name="operations">Cherry-pick, revert, new branch / tag; null = no commit menu.</param>
    public CommitLogViewModel(RepositoryTabLayout layout, GitRepositoryClient? client,
        Action<FileChangeViewModel, int?>? openInEditor, Action<string, string?>? annotate = null,
        CommitOperations? operations = null, IRemoteHost? host = null)
    {
        Layout = layout;
        _client = client;
        _operations = operations;
        _host = host;
        Details = new CommitDetailsViewModel(layout, client, openInEditor, annotate);
        if (client is not null)
            _ = LoadIdentityAsync(client);
    }

    /// <summary>
    /// Finds out who "you" are (this repository's user.email, then your login, name and picture on
    /// the hosting service), so your commits show your avatar. Best effort: offline or signed out, rows just
    /// show initials.
    /// </summary>
    private async Task LoadIdentityAsync(GitRepositoryClient client)
    {
        try
        {
            if (await client.GetConfigAsync("user.email") is { } email)
                _myEmails.Add(email);
            if (await client.GetConfigAsync("user.name") is { } name)
                _myNames.Add(name);
            RebuildRows();

            if (_host is not null && await _host.GetMyIdentityAsync() is { } identity)
            {
                _isMyHostEmail = identity.IsOwnEmail;
                foreach (var hostName in identity.Names)
                    _myNames.Add(hostName);
                _myAvatar.Image = identity.Avatar;
                RebuildRows();
            }
        }
        catch (Exception)
        {
            // Only decoration: never let it disturb the history.
        }
    }

    /// <summary>Your commits: your e-mail, an e-mail the host says is yours (GitHub's no-reply address), or your name.</summary>
    private bool IsMine(GitCommit commit) =>
        _myEmails.Contains(commit.AuthorEmail)
        || _isMyHostEmail?.Invoke(commit.AuthorEmail) == true
        || _myNames.Contains(commit.AuthorName);

    private AvatarViewModel? AvatarFor(GitCommit commit) => IsMine(commit) ? _myAvatar : null;

    /// <summary>Recreates the rows with the current idea of "you" (the graph is kept).</summary>
    private void RebuildRows()
    {
        if (_allRows.Count == 0)
            return;
        var now = DateTimeOffset.Now;
        _allRows = _allRows.Select(r => new CommitRowViewModel(r.Commit, r.Graph, r.GraphLanes, now, AvatarFor(r.Commit))).ToList();
        ApplyFilter();
    }

    /// <summary>Panel sizes (details height, file list width), shared by all repository tabs.</summary>
    public RepositoryTabLayout Layout { get; }

    /// <summary>The selected commit's message, files and diff.</summary>
    public CommitDetailsViewModel Details { get; }

    /// <summary>The rows shown: all loaded commits, or the ones matching <see cref="Filter"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows), nameof(PlaceholderText))]
    private IReadOnlyList<CommitRowViewModel> _rows = [];

    public bool HasRows => Rows.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaceholderText))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaceholderText))]
    private string? _error;

    /// <summary>Search box: subject, author, commit id or branch/tag name (case-insensitive).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltering))]
    private string _filter = "";

    /// <summary>
    /// The graph is hidden while filtering: with commits left out, the lines between the
    /// remaining ones would be wrong.
    /// </summary>
    public bool IsFiltering => !string.IsNullOrWhiteSpace(Filter);

    /// <summary>More commits exist beyond the loaded ones.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool _hasMore;

    /// <summary>"1,000 commits" (or "12 of 1,000 commits" while filtering).</summary>
    [ObservableProperty] private string _countText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CherryPickCommand), nameof(RevertCommand), nameof(NewBranchHereCommand), nameof(NewTagHereCommand))]
    private CommitRowViewModel? _selected;

    /// <summary>A commit to select once the history has loaded (a branch clicked in the sidebar before that).</summary>
    private string? _pendingReveal;

    /// <summary>
    /// Selects <paramref name="commitId"/> (Fork: clicking a branch in the sidebar shows it in the
    /// history). The list scrolls to it. If the history is still loading, it happens when it
    /// arrives; a search that hides the commit is cleared.
    /// </summary>
    public void Reveal(string commitId)
    {
        _pendingReveal = commitId;
        if (_loadedSignature is not null && !IsLoading)
            ApplyReveal();
    }

    private void ApplyReveal()
    {
        if (_pendingReveal is not { } id)
            return;
        var row = _allRows.FirstOrDefault(r => r.Commit.Id == id);
        if (row is null)
            return; // not loaded (older than the loaded pages) or not there yet: keep waiting
        _pendingReveal = null;
        if (!Rows.Contains(row))
            Filter = "";
        Selected = row;
    }

    /// <summary>What the list area says when there are no rows (empty repository, no matches, error).</summary>
    public string? PlaceholderText =>
        HasRows ? null
        : Error is not null ? "Couldn't read the history: " + Error
        : IsLoading ? null
        : IsFiltering ? "No commits match the search."
        : _loadedSignature is null ? null
        : "No commits yet.";

    /// <summary>The section was shown: load the history if it hasn't been yet (<paramref name="signature"/>: see <see cref="RefreshIfChangedAsync"/>).</summary>
    public Task EnsureLoadedAsync(string signature) =>
        _loadedSignature is null && !IsLoading ? LoadAsync(signature) : Task.CompletedTask;

    /// <summary>
    /// Called after every repository refresh with a fingerprint of the branch tips and HEAD;
    /// reloads only when it changed (a commit, checkout, fetch, …) and the history was shown before.
    /// </summary>
    public Task RefreshIfChangedAsync(string signature)
    {
        if (_loadedSignature == "")
        {
            // Loaded before the tab's first refresh knew the fingerprint: that load is current.
            _loadedSignature = signature;
            return Task.CompletedTask;
        }
        return _loadedSignature is not null && _loadedSignature != signature ? LoadAsync(signature) : Task.CompletedTask;
    }

    // ---- Commit menu (G8) ------------------------------------------------------------------------

    private bool HasSelection() => Selected is not null && _operations is { IsAvailable: true };

    /// <summary>Copy the selected commit's changes onto the current branch.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task CherryPickAsync() => _operations!.CherryPickAsync(Selected!.Commit);

    /// <summary>A new commit that undoes the selected one.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RevertAsync() => _operations!.RevertAsync(Selected!.Commit);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task NewBranchHereAsync() => _operations!.NewBranchAtAsync(Selected!.Commit);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task NewTagHereAsync() => _operations!.NewTagAsync(Selected!.Commit);

    [RelayCommand(CanExecute = nameof(HasMore))]
    private Task LoadMoreAsync()
    {
        _limit += PageSize;
        return LoadAsync(_loadedSignature ?? "");
    }

    private async Task LoadAsync(string signature)
    {
        if (_client is null)
            return;

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var commits = await _client.GetLogAsync(_limit, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            var graph = CommitGraph.Layout(commits);
            var lanes = Math.Clamp(graph.Count == 0 ? 1 : graph.Max(r => r.LaneCount), 1, MaxGraphLanes);
            var now = DateTimeOffset.Now;
            _allRows = commits.Select((c, i) => new CommitRowViewModel(c, graph[i], lanes, now, AvatarFor(c))).ToList();
            _loadedSignature = signature;
            HasMore = commits.Count >= _limit;
            Error = null;
            ApplyFilter();
            ApplyReveal();
        }
        catch (OperationCanceledException)
        {
            // A newer load replaced this one.
        }
        catch (Exception ex)
        {
            // Started fire-and-forget (section shown, refresh): show the problem in the list area.
            _loadedSignature ??= signature;
            _allRows = [];
            Error = ex.Message;
            ApplyFilter();
        }
        finally
        {
            if (_loadCts == cts)
                IsLoading = false;
        }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedChanged(CommitRowViewModel? value)
    {
        // Same commit after a reload (new row object): keep the details as they are (no flicker,
        // the selected file stays selected).
        if (value?.Commit.Id is { } id && Details.Commit?.Id == id)
            return;
        _ = Details.LoadAsync(value?.Commit);
    }

    /// <summary>Shows the rows matching the filter, keeping the selected commit selected (or picking the first).</summary>
    private void ApplyFilter()
    {
        var keep = Selected?.Commit.Id;
        var text = Filter.Trim();
        Rows = text.Length == 0 ? _allRows : _allRows.Where(r => r.Matches(text)).ToList();
        CountText = text.Length == 0
            ? $"{_allRows.Count:N0}{(HasMore ? "+" : "")} commits"
            : $"{Rows.Count:N0} of {_allRows.Count:N0} commits";

        Selected = (keep is null ? null : Rows.FirstOrDefault(r => r.Commit.Id == keep)) ?? Rows.FirstOrDefault();
    }
}

/// <summary>One commit in the All Commits list.</summary>
public sealed class CommitRowViewModel
{
    /// <param name="avatar">The author's picture (yours, for your commits), or null: initials.</param>
    public CommitRowViewModel(GitCommit commit, GraphRow graph, int graphLanes, DateTimeOffset now, AvatarViewModel? avatar = null)
    {
        Commit = commit;
        Avatar = avatar;
        Graph = graph;
        GraphLanes = graphLanes;
        Date = RelativeTime.Format(commit.AuthorDate, now);
        Refs = commit.Refs.Select(r => new RefBadgeViewModel(r)).ToList();
        IsHead = commit.Refs.Any(r => r.IsHead);
    }

    public GitCommit Commit { get; }
    public GraphRow Graph { get; }

    /// <summary>Width of the graph column in lanes (the same for every row).</summary>
    public int GraphLanes { get; }

    public string Subject => Commit.Subject;
    public string Author => Commit.AuthorName;

    /// <summary>The author's picture, or null (the Avatar control shows the first letter of <see cref="Author"/>).</summary>
    public AvatarViewModel? Avatar { get; }
    public string Date { get; }
    public string ShortId => Commit.ShortId;

    /// <summary>The checked-out commit: its dot gets a ring and its subject is bold.</summary>
    public bool IsHead { get; }

    /// <summary>Branch / tag badges before the subject.</summary>
    public IReadOnlyList<RefBadgeViewModel> Refs { get; }

    public bool HasRefs => Refs.Count > 0;

    /// <summary>Full date and author e-mail, for the tooltip.</summary>
    public string DateToolTip => $"{Commit.AuthorDate.ToLocalTime():yyyy-MM-dd HH:mm} · {Commit.AuthorEmail}";

    public bool Matches(string text) =>
        Commit.Subject.Contains(text, StringComparison.OrdinalIgnoreCase)
        || Commit.AuthorName.Contains(text, StringComparison.OrdinalIgnoreCase)
        || Commit.Id.StartsWith(text, StringComparison.OrdinalIgnoreCase)
        || Commit.Refs.Any(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>Accessible name: what a screen reader says for the row.</summary>
    public override string ToString() =>
        $"{Subject}, {Author}, {Date}{(HasRefs ? ", " + string.Join(", ", Refs) : "")}";
}

/// <summary>A branch / tag badge on a commit row.</summary>
public sealed class RefBadgeViewModel(GitRefLabel label)
{
    public string Name => label.Name;
    /// <summary>A local branch other than the checked-out one (that one gets the accent color).</summary>
    public bool IsLocal => label.Kind == GitRefKind.LocalBranch && !label.IsHead;
    public bool IsRemote => label.Kind == GitRefKind.RemoteBranch;
    public bool IsTag => label.Kind == GitRefKind.Tag;

    /// <summary>The checked-out branch (or a detached HEAD).</summary>
    public bool IsHead => label.IsHead;

    public string ToolTip => label.Kind switch
    {
        GitRefKind.Head => "HEAD (detached): the checked-out commit",
        GitRefKind.Tag => $"Tag {Name}",
        GitRefKind.RemoteBranch => $"Remote branch {Name}",
        _ => label.IsHead ? $"Branch {Name} (checked out)" : $"Branch {Name}",
    };

    public override string ToString() => Name;
}
