using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Controls;
using DevHub.Sdk.Core.Formatting;
using DevHub.Git.Core;
using DevHub.Services;

namespace DevHub.ViewModels.Documents;

/// <summary>
/// JetBrains' "Annotate with Git Blame" as an editor tab: every line of a file with the commit
/// that last changed it (id, author, date in a gutter, once per block of lines). Opened from the
/// diff pane's Blame button: for a local change the file as it is on disk (lines changed since
/// the last commit show as "Not committed yet"); in All Commits / Stashes, the file as it was
/// in that commit. Read-only.
/// </summary>
public sealed partial class BlameDocumentViewModel : DocumentViewModelBase
{
    public const string IdPrefix = "Blame:";

    private readonly GitRepositoryClient? _client;
    private readonly RepositoryFileOpener _editor;
    private CancellationTokenSource? _loadCts;

    /// <param name="commitId">The commit to blame the file in; null = the file on disk now.</param>
    public BlameDocumentViewModel(string repositoryPath, string filePath, string? commitId, GitRepositoryClient? client,
        RepositoryFileOpener editor)
    {
        RepositoryPath = repositoryPath;
        FilePath = filePath;
        CommitId = commitId;
        _client = client;
        _editor = editor;
        var name = filePath[(filePath.LastIndexOf('/') + 1)..];
        Title = commitId is null ? $"{name} (blame)" : $"{name} @ {commitId[..Math.Min(8, commitId.Length)]}";
        Subtitle = commitId is null ? $"{filePath} · as on disk now" : $"{filePath} · in commit {commitId[..Math.Min(8, commitId.Length)]}";
        _ = LoadAsync();
    }

    public override string Id => BlameDocumentFactory.IdFor(RepositoryPath, FilePath, CommitId);
    public override string Title { get; }
    public override string Icon => IconKind.Commit;
    public override bool FillsEditorArea => true;

    public string RepositoryPath { get; }

    public override string? ContextPath => RepositoryPath;
    public string FilePath { get; }
    public string? CommitId { get; }

    /// <summary>"src/App.cs · as on disk now" / "… · in commit 1a2b3c4d".</summary>
    public string Subtitle { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows), nameof(PlaceholderText))]
    private IReadOnlyList<BlameRowViewModel> _rows = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>"12 commits, 3 authors".</summary>
    [ObservableProperty] private string _summary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaceholderText))]
    private string? _error;

    [ObservableProperty] private BlameRowViewModel? _selected;

    public string? PlaceholderText => HasRows ? null : Error;

    /// <summary>Re-reads the blame (the Refresh button; and on activation for the file on disk, which may have changed).</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_client is null)
        {
            Error = "Git is not installed.";
            return;
        }

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        try
        {
            var lines = await _client.GetBlameAsync(FilePath, CommitId, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            var now = DateTimeOffset.Now;
            var rows = new List<BlameRowViewModel>(lines.Count);
            var block = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                // A new block starts where the commit changes: only its first line shows the gutter.
                var first = i == 0 || lines[i - 1].Commit.Id != lines[i].Commit.Id;
                if (first && i > 0)
                    block++;
                rows.Add(new BlameRowViewModel(lines[i], first, block % 2 == 1, now));
            }

            var keep = Selected?.LineNumber;
            Rows = rows;
            Selected = keep is null ? null : rows.FirstOrDefault(r => r.LineNumber == keep);
            // Uncommitted lines (git's "Not Committed Yet") are neither a commit nor an author.
            var committed = lines.Where(l => !l.Commit.IsUncommitted).ToList();
            var commits = committed.Select(l => l.Commit.Id).Distinct().Count();
            var authors = committed.Select(l => l.Commit.Author).Distinct().Count();
            Summary = lines.Count == 0 ? "Empty file"
                : $"{lines.Count:N0} line{(lines.Count == 1 ? "" : "s")} · {commits:N0} commit{(commits == 1 ? "" : "s")} · {authors:N0} author{(authors == 1 ? "" : "s")}";
            Error = lines.Count == 0 ? "The file is empty." : null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Rows = [];
            Summary = "";
            // git: "no such path 'x' in HEAD" for a file that was never committed.
            Error = ex is GitException { StandardError: var e } && e.Contains("no such path", StringComparison.Ordinal)
                ? $"{FilePath} has no history yet: commit it first."
                : "Couldn't blame the file: " + (ex is GitException g ? g.StandardError.Trim() : ex.Message);
        }
    }

    /// <summary>
    /// F4 / double-click / right-click: the file in the external editor, at the selected line. Always
    /// the file as it is on disk now (for a blame in an older commit the line may have moved since).
    /// </summary>
    [RelayCommand]
    private void OpenInEditor() => _editor.Open(RepositoryPath, FilePath, Selected?.LineNumber);

    // The file on disk may have changed while another tab was in front.
    protected override void OnActivationChanged(bool isActive)
    {
        if (isActive && CommitId is null && HasRows)
            _ = LoadAsync();
    }
}

/// <summary>One line in a blame tab: gutter (on a block's first line), number, text.</summary>
public sealed class BlameRowViewModel
{
    public BlameRowViewModel(BlameLine line, bool isBlockStart, bool isOddBlock, DateTimeOffset now)
    {
        LineNumber = line.LineNumber;
        Text = line.Text;
        IsBlockStart = isBlockStart;
        IsOddBlock = isOddBlock;
        var c = line.Commit;
        IsUncommitted = c.IsUncommitted;
        ShortId = isBlockStart ? (c.IsUncommitted ? "" : c.ShortId) : "";
        Author = isBlockStart ? (c.IsUncommitted ? "Not committed yet" : c.Author) : "";
        Date = isBlockStart && !c.IsUncommitted ? RelativeTime.Format(c.AuthorDate, now) : "";
        ToolTip = c.IsUncommitted
            ? "Changed since the last commit"
            : $"{c.ShortId} · {c.Author} <{c.AuthorEmail}> · {c.AuthorDate.ToLocalTime():yyyy-MM-dd HH:mm}\n{c.Summary}";
    }

    public int LineNumber { get; }
    public string Text { get; }

    /// <summary>First line of a run from one commit: the gutter shows id, author, date.</summary>
    public bool IsBlockStart { get; }

    /// <summary>Every other block gets a shaded gutter, so neighbouring blocks stand apart.</summary>
    public bool IsOddBlock { get; }

    public bool IsUncommitted { get; }
    public string ShortId { get; }
    public string Author { get; }
    public string Date { get; }

    /// <summary>The commit in full (every line, not only the block's first).</summary>
    public string ToolTip { get; }

    public override string ToString() => $"Line {LineNumber}: {Text}";
}
