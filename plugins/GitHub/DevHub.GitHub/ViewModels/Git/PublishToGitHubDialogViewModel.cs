using CommunityToolkit.Mvvm.ComponentModel;
using DevHub.GitHub.Core;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Git;

/// <summary>
/// Publish to GitHub (JetBrains' "Share Project on GitHub"): account, repository name,
/// description, private or public, and the remote name to add. OK creates the empty repository
/// on GitHub right away, so a refusal (name taken, signed out) is shown here and the dialog stays
/// open; the caller then adds the remote and pushes.
/// </summary>
public sealed partial class PublishToGitHubDialogViewModel : DialogViewModelBase
{
    private readonly GitHubService _github;
    private readonly IReadOnlyList<string> _existingRemotes;

    public PublishToGitHubDialogViewModel(GitHubService github, IReadOnlyList<string> accounts, string repositoryName,
        IReadOnlyList<string> existingRemotes)
    {
        _github = github;
        _existingRemotes = existingRemotes;
        Accounts = accounts;
        _account = accounts[0];
        // GitHub names allow letters, digits, "-", "_", "."; spaces become "-" (as on github.com).
        _name = new string(repositoryName.Trim().Replace(' ', '-')
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        _remoteName = existingRemotes.Contains("origin") ? "github" : "origin";
    }

    public override string Title => "Publish to GitHub";
    public override string OkText => "Publish";
    public override bool FitsContent => true;
    public override double Width => 480;
    public override double MinWidth => 400;
    public override double MinHeight => 0;

    public IReadOnlyList<string> Accounts { get; }
    public bool HasSeveralAccounts => Accounts.Count > 1;

    [ObservableProperty] private string _account;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description = "";

    /// <summary>Private by default: publishing code to the world should be a deliberate choice.</summary>
    [ObservableProperty] private bool _isPrivate = true;

    [ObservableProperty] private string _remoteName;
    [ObservableProperty] private string? _error;

    /// <summary>"github.com/Naurid/my-app" as it will be.</summary>
    public string Preview => $"github.com/{Account}/{Name.Trim()}";

    partial void OnAccountChanged(string value) => OnPropertyChanged(nameof(Preview));
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Preview));

    /// <summary>The repository GitHub created (set when OK succeeded).</summary>
    public GitHubRepository? Created { get; private set; }

    protected override async Task<bool> ApplyChangesAsync()
    {
        Name = Name.Trim();
        RemoteName = RemoteName.Trim();
        if (!GitHubService.IsValidRepositoryName(Name))
        {
            Error = "Use letters, digits, \"-\", \"_\" or \".\" for the name (up to 100 characters).";
            return false;
        }
        if (RemoteName.Length == 0 || RemoteName.Any(char.IsWhiteSpace) || RemoteName.StartsWith('-'))
        {
            Error = "Enter a remote name without spaces (usually \"origin\").";
            return false;
        }
        if (_existingRemotes.Contains(RemoteName))
        {
            Error = $"This repository already has a remote named {RemoteName}: pick another name.";
            return false;
        }

        // Created already (OK pressed again after a later step failed): don't create a second one.
        if (Created is not null)
            return true;
        try
        {
            Error = null;
            Created = await _github.CreateRepositoryAsync(Account, Name, Description, IsPrivate);
            return true;
        }
        catch (GitHubException ex)
        {
            Error = ex.Message;
            return false;
        }
    }
}
