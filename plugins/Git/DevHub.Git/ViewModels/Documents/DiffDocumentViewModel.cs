using Avalonia.Threading;
using DevHub.Controls;
using DevHub.Git.Core;
using DevHub.Services;
using DevHub.ViewModels.Git;

namespace DevHub.ViewModels.Documents;

/// <summary>
/// One file's diff as its own editor tab ("Open in New Tab" in the Local Changes diff pane), for
/// a bigger view. Identified by repository + file + side (staged / unstaged), so it can be
/// restored at startup. Stays live: it re-reads the diff when the repository changes on disk.
/// </summary>
public sealed class DiffDocumentViewModel : DocumentViewModelBase, IDisposable
{
    public const string IdPrefix = "Diff:";

    private readonly GitRepositoryClient? _client;
    private readonly RepositoryWatcher? _watcher;
    private CancellationTokenSource? _refreshCts;
    private bool _disposed;

    /// <param name="repositoryPath">Repository root folder.</param>
    /// <param name="filePath">Git path of the file (relative, forward slashes).</param>
    /// <param name="staged">The staged side (index vs HEAD), else the unstaged side.</param>
    /// <param name="editor">Opens the file in the external editor for the repository's language.</param>
    /// <param name="layout">Shared view choices (unified / side-by-side).</param>
    public DiffDocumentViewModel(string repositoryPath, string filePath, bool staged, GitRepositoryClient? client,
        RepositoryFileOpener editor, RepositoryTabLayout layout)
    {
        RepositoryPath = repositoryPath;
        FilePath = filePath;
        IsStaged = staged;
        _client = client;
        var trimmed = filePath.TrimEnd('/');
        var name = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        Title = staged ? name + " (staged)" : name;
        Diff = new DiffViewModel(layout, client, (file, line) => editor.Open(repositoryPath, file.Change.Path, line),
            annotate: (file, commitId) => OpenDocument?.Invoke(BlameDocumentFactory.IdFor(repositoryPath, file, commitId)));

        if (client is null)
        {
            Diff.ShowMessage("Git is not installed.");
            return;
        }

        try
        {
            _watcher = new RepositoryWatcher(repositoryPath);
            _watcher.Changed += OnRepositoryChanged;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Folder gone: the refresh below says so.
        }
        _ = RefreshAsync();
    }

    public override string Id => DiffDocumentFactory.IdFor(RepositoryPath, FilePath, IsStaged);

    /// <summary>The file name, like a file tab in JetBrains; "(staged)" tells the two sides of one file apart.</summary>
    public override string Title { get; }

    public override string Icon => IconKind.Edit;

    /// <summary>The diff runs edge to edge, like in Local Changes.</summary>
    public override bool FillsEditorArea => true;

    public string RepositoryPath { get; }

    public override string? ContextPath => RepositoryPath;
    public string FilePath { get; }
    public bool IsStaged { get; }

    public DiffViewModel Diff { get; }

    /// <summary>Finds the file in the current status and shows its diff, or says it has no changes any more.</summary>
    public async Task RefreshAsync()
    {
        if (_client is null || _disposed)
            return;

        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();
        try
        {
            var status = await _client.GetStatusAsync(cts.Token);
            var change = status.Changes.FirstOrDefault(c => c.Path == FilePath
                                                            && (IsStaged ? c.IsStaged : c.IsUnstaged || c.IsConflicted));
            if (cts.IsCancellationRequested)
                return;

            if (change is null)
                Diff.ShowMessage($"No {(IsStaged ? "staged" : "unstaged")} changes in {FilePath} any more.");
            else
                await Diff.LoadAsync(new FileChangeViewModel(change, IsStaged));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Diff.ShowMessage("Couldn't read the repository: " + ex.Message);
        }
    }

    // The watcher already keeps it current; activation covers changes it can't see (e.g. after sleep).
    protected override void OnActivationChanged(bool isActive)
    {
        if (isActive)
            _ = RefreshAsync();
    }

    private void OnRepositoryChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => _ = RefreshAsync());

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_watcher is not null)
        {
            _watcher.Changed -= OnRepositoryChanged;
            _watcher.Dispose();
        }
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
    }
}
