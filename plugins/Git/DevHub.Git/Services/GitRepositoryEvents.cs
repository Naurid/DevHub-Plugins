using Avalonia.Threading;
using DevHub.Git.Core;
using DevHub.Integration;

namespace DevHub.Services;

/// <summary>
/// Git's side of <see cref="IRepositoryEvents"/> (offered to other plugins, DevHub.Integration): each repository tab
/// lets it watch its client (commits, fetches, pulls and pushes made from DevHub) and reports the branch every
/// status refresh finds (so a checkout from a terminal is noticed too). Raised on the UI thread; a handler that
/// throws doesn't stop the others.
/// </summary>
public sealed class GitRepositoryEvents : IRepositoryEvents
{
    private readonly Dictionary<string, (string? Branch, string? Commit)> _heads = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<RepositoryEventArgs>? BranchChanged;
    public event EventHandler<RepositoryEventArgs>? Committed;
    public event EventHandler<PushEventArgs>? Pushed;
    public event EventHandler<RepositoryEventArgs>? Fetched;

    /// <summary>Reports what <paramref name="client"/> does until the returned subscription is disposed (the tab closes).</summary>
    public IDisposable Watch(GitRepositoryClient client)
    {
        void OnCompleted(object? sender, GitOperationCompleted e) => _ = ReportAsync(client, e);
        client.OperationCompleted += OnCompleted;
        return new Subscription(() => client.OperationCompleted -= OnCompleted);
    }

    /// <summary>
    /// What a status refresh found: when the branch differs from the last one seen for this repository, BranchChanged
    /// (not the first time a repository is seen: opening a tab isn't a change).
    /// </summary>
    public void ReportHead(string repositoryFolder, string? branch, string? commitId)
    {
        var known = _heads.TryGetValue(repositoryFolder, out var previous);
        _heads[repositoryFolder] = (branch, commitId);
        if (known && !string.Equals(previous.Branch, branch, StringComparison.Ordinal))
            Raise(BranchChanged, new RepositoryEventArgs(repositoryFolder, branch, commitId));
    }

    private async Task ReportAsync(GitRepositoryClient client, GitOperationCompleted e)
    {
        RepoStatus status;
        try
        {
            status = await client.GetStatusAsync();
        }
        catch (Exception ex) when (ex is GitException or IOException or InvalidOperationException)
        {
            return; // the repository went away right after; nothing to report
        }
        var folder = client.RepositoryPath;
        var args = new RepositoryEventArgs(folder, status.Branch, e.CommitId ?? status.CommitId);
        switch (e.Operation)
        {
            case GitOperation.Committed:
                Raise(Committed, args);
                break;
            case GitOperation.Fetched:
            case GitOperation.Pulled:
                Raise(Fetched, args);
                break;
            case GitOperation.Pushed when status is { Branch: { } branch, Upstream: { } upstream, CommitId: { } commit }:
                var slash = upstream.IndexOf('/');
                var (remote, remoteBranch) = slash > 0 ? (upstream[..slash], upstream[(slash + 1)..]) : (upstream, branch);
                Raise(Pushed, new PushEventArgs(folder, branch, remote, remoteBranch, commit));
                break;
        }
    }

    private void Raise<T>(EventHandler<T>? handlers, T args)
    {
        if (handlers is null)
            return;
        void Run()
        {
            foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<T>>())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception)
                {
                    // Another plugin's handler failed: its problem; the rest still hear about it.
                }
            }
        }
        if (Dispatcher.UIThread.CheckAccess())
            Run();
        else
            Dispatcher.UIThread.Post(Run);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            _dispose?.Invoke();
            _dispose = null;
        }
    }
}
