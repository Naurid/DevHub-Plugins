using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.GitHub.Core;
using DevHub.Services;

namespace DevHub.ViewModels.Dialogs.Settings;

/// <summary>
/// Settings → GitHub: the GitHub accounts signed in through Git Credential Manager (the sign-in
/// git itself uses for github.com), with Sign In… and Sign Out…. Changes take effect at once
/// (they're GCM's, not DevHub settings), so the page never needs Apply.
/// </summary>
public sealed partial class GitHubSettingsPageViewModel : SettingsPageViewModelBase
{
    private readonly GitHubSession _session;

    public GitHubSettingsPageViewModel(GitHubSession session)
    {
        _session = session;
        _ = LoadAsync();
    }

    public override string Title => "GitHub";
    public override int Order => 300;
    public override string Keywords => "github account sign in login logout token credential manager clone publish";

    public ObservableCollection<GitHubAccountRowViewModel> Accounts { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccounts), nameof(EmptyText))]
    private bool _isLoading = true;

    public bool HasAccounts => Accounts.Count > 0;

    /// <summary>Why there's no account list, or null while loading / when there are accounts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string? _unavailableReason;

    public string? EmptyText => IsLoading || HasAccounts ? null
        : UnavailableReason ?? "Not signed in. Sign in to push to GitHub, publish repositories and clone your own.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private bool _isBusy;

    // Nothing to apply: signing in and out happens immediately, in Git Credential Manager.
    public override void Apply()
    {
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        if (!_session.IsAvailable)
            UnavailableReason = "Git is not installed, so there's no Git Credential Manager to sign in with.";
        else if (!await _session.IsCredentialManagerAvailableAsync())
            UnavailableReason = "Git Credential Manager isn't installed. It comes with Git for Windows (git-scm.com): " +
                                "reinstall or update Git and keep \"Git Credential Manager\" ticked.";
        else
            UnavailableReason = null;

        var accounts = UnavailableReason is null ? await _session.ListAccountsAsync() : [];
        Accounts.Clear();
        foreach (var account in accounts)
        {
            var row = new GitHubAccountRowViewModel(account, a => _ = SignOutAsync(a));
            Accounts.Add(row);
            _ = row.CheckAsync(_session);
        }
        IsLoading = false;
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(EmptyText));
    }

    private bool CanSignIn() => UnavailableReason is null && !IsBusy;

    /// <summary>Git Credential Manager's sign-in window; adds an account (or refreshes an expired one).</summary>
    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        IsBusy = true;
        try
        {
            await _session.SignInAsync();
        }
        finally
        {
            IsBusy = false;
        }
        await LoadAsync();
    }

    private async Task SignOutAsync(string account)
    {
        await _session.SignOutAsync(account);
        await LoadAsync();
    }
}

/// <summary>A signed-in account: its login, and (once checked with GitHub) its name or "sign-in expired".</summary>
public sealed partial class GitHubAccountRowViewModel : ObservableObject
{
    public GitHubAccountRowViewModel(string account, Action<string> signOut)
    {
        Account = account;
        SignOutCommand = new RelayCommand(() => signOut(account));
    }

    public string Account { get; }

    /// <summary>"Gabor Vilics · signed in", "checking…", or what's wrong.</summary>
    [ObservableProperty] private string _detail = "Checking with GitHub…";

    /// <summary>The account's GitHub picture, once downloaded.</summary>
    [ObservableProperty] private byte[]? _avatar;

    /// <summary>GitHub didn't accept the stored sign-in: Sign In again refreshes it.</summary>
    [ObservableProperty] private bool _isProblem;

    public RelayCommand SignOutCommand { get; }

    /// <summary>Asks GitHub who this is (proves the sign-in works, with a read-only call).</summary>
    public async Task CheckAsync(GitHubSession session)
    {
        try
        {
            var (user, avatar) = await session.GetProfileAsync(Account);
            Avatar = avatar;
            Detail = user.Name is { Length: > 0 } name ? $"{name} · signed in" : "Signed in";
        }
        catch (GitHubException ex)
        {
            Detail = ex.Message;
            IsProblem = true;
        }
        catch (Exception ex)
        {
            Detail = "Couldn't check the sign-in: " + ex.Message;
            IsProblem = true;
        }
    }

    public override string ToString() => $"{Account}, {Detail}";
}
