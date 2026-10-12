using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Git.Core;
using DevHub.Integration;
using DevHub.Services;

namespace DevHub.ViewModels.Git;

/// <summary>
/// The commit box under the file lists (Fork / JetBrains): message, Amend, Commit (Ctrl+Enter).
/// Kept per repository tab, so a half-written message survives switching tabs.
/// </summary>
public partial class CommitViewModel : ViewModelBase
{
    private readonly GitRepositoryClient? _client;
    private readonly IStatusService _status;
    private readonly Func<bool> _hasStagedChanges;
    private readonly Func<Task> _afterCommit;
    private readonly Func<Task>? _push;
    private readonly IReadOnlyList<ICommitMessageSuggester> _suggesters;
    private readonly Func<CommitContext?>? _context;
    private string? _askedFor; // the context the current suggestion is for (branch + staged files)

    /// <summary>The message Amend filled in, to clear it again when Amend is turned off untouched.</summary>
    private string? _amendMessage;

    /// <param name="hasStagedChanges">Whether anything is staged (a commit needs something, unless amending).</param>
    /// <param name="afterCommit">Refreshes the repository after a commit.</param>
    /// <param name="push">Pushes the current branch (for Commit and Push); null hides that button.</param>
    /// <param name="suggesters">Other plugins' message suggestions (DevHub.Integration), asked while the message is empty.</param>
    /// <param name="context">What would be committed (repository, branch, staged files), for the suggesters.</param>
    public CommitViewModel(GitRepositoryClient? client, IStatusService status, Func<bool> hasStagedChanges,
        Func<Task> afterCommit, Func<Task>? push = null,
        IReadOnlyList<ICommitMessageSuggester>? suggesters = null, Func<CommitContext?>? context = null)
    {
        _client = client;
        _status = status;
        _hasStagedChanges = hasStagedChanges;
        _afterCommit = afterCommit;
        _push = push;
        _suggesters = suggesters ?? [];
        _context = context;
    }

    /// <summary>A message another plugin suggests (e.g. "DEV-12: " from the branch name), shown while the box is empty; never put in without a click.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSuggestion))]
    private string? _suggestion;

    public bool ShowsSuggestion => Suggestion is not null && string.IsNullOrWhiteSpace(Message) && !IsAmend;

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(ShowsSuggestion));

    /// <summary>Puts the suggestion in the box (the user edits it from there).</summary>
    [RelayCommand]
    private void UseSuggestion()
    {
        if (Suggestion is null)
            return;
        Message = Suggestion;
        RequestFocus();
    }

    /// <summary>Asks the suggesters again when what would be committed changed (first answer wins; slow or failing ones are skipped).</summary>
    private async Task RefreshSuggestionAsync()
    {
        if (_suggesters.Count == 0 || _context?.Invoke() is not { } context)
            return;
        var key = context.Branch + "\n" + string.Join("\n", context.StagedFiles);
        if (key == _askedFor)
            return;
        _askedFor = key;
        string? found = null;
        foreach (var suggester in _suggesters)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                found = await suggester.SuggestAsync(context, timeout.Token);
            }
            catch (Exception)
            {
                found = null; // another plugin's problem: no suggestion from it
            }
            if (!string.IsNullOrWhiteSpace(found))
                break;
        }
        if (key == _askedFor)
            Suggestion = string.IsNullOrWhiteSpace(found) ? null : found;
    }

    public bool CanPush => _push is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(CommitAndPushCommand))]
    private string _message = "";

    /// <summary>Replace the last commit (its message is filled in to edit) instead of adding one.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(CommitAndPushCommand))]
    private bool _isAmend;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(CommitAndPushCommand))]
    private bool _isCommitting;

    /// <summary>Why the last commit failed (e.g. a pre-commit hook's output), or null.</summary>
    [ObservableProperty] private string? _error;

    /// <summary>Incremented to put the cursor in the message box (Ctrl+K); see Controls/FocusRequest.</summary>
    [ObservableProperty] private int _focusRequest;

    public string CommitButtonText => IsAmend ? "Amend Commit" : "Commit";

    /// <summary>Puts the cursor in the message box.</summary>
    public void RequestFocus() => FocusRequest++;

    /// <summary>Re-checks whether Commit is possible (the staged list changed).</summary>
    public void StagedChanged()
    {
        CommitCommand.NotifyCanExecuteChanged();
        CommitAndPushCommand.NotifyCanExecuteChanged();
        _ = RefreshSuggestionAsync();
    }

    private bool CanCommit() =>
        _client is not null && !IsCommitting && !string.IsNullOrWhiteSpace(Message)
        && (_hasStagedChanges() || IsAmend);

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private Task CommitAsync() => CommitCoreAsync();

    private bool CanCommitAndPush() => CanCommit() && _push is not null;

    /// <summary>JetBrains' Commit and Push (Ctrl+Alt+K): commit, then push the branch (no extra question).</summary>
    [RelayCommand(CanExecute = nameof(CanCommitAndPush))]
    private async Task CommitAndPushAsync()
    {
        if (await CommitCoreAsync())
            await _push!();
    }

    /// <summary>Commits; true when it worked (the message is then cleared and the repository refreshed).</summary>
    private async Task<bool> CommitCoreAsync()
    {
        var committed = false;
        IsCommitting = true;
        Error = null;
        try
        {
            var id = await _client!.CommitAsync(Message, IsAmend);
            var subject = Message.Split('\n')[0].Trim();
            _status.Show($"{(IsAmend ? "Amended" : "Committed")} {id[..7]}: {subject}");
            _amendMessage = null;
            Message = "";
            IsAmend = false;
            committed = true;
        }
        catch (Exception ex)
        {
            // Hooks, signing, "nothing to commit"…: git's own words, where the user is looking.
            Error = ex is GitException git && git.StandardError.Trim() is { Length: > 0 } stderr ? stderr : ex.Message;
            _status.Show("Commit failed.");
        }
        finally
        {
            IsCommitting = false;
        }
        await _afterCommit();
        return committed;
    }

    partial void OnIsAmendChanged(bool value)
    {
        OnPropertyChanged(nameof(CommitButtonText));
        OnPropertyChanged(nameof(ShowsSuggestion));
        if (value)
            _ = LoadLastMessageAsync();
        else if (_amendMessage is not null && Message == _amendMessage)
            Message = ""; // untouched: take the old message away again
    }

    // JetBrains fills in the last message when Amend is ticked, if the box is empty.
    private async Task LoadLastMessageAsync()
    {
        if (_client is null || !string.IsNullOrWhiteSpace(Message))
            return;

        var last = await _client.GetLastCommitMessageAsync();
        if (last is null)
        {
            _status.Show("Nothing to amend: there are no commits yet.");
            IsAmend = false;
            return;
        }
        if (IsAmend && string.IsNullOrWhiteSpace(Message))
            Message = _amendMessage = last;
    }
}
