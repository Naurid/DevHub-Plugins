using CommunityToolkit.Mvvm.Input;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Git;

/// <summary>What the user picked in <see cref="NoRemoteDialogViewModel"/>.</summary>
public enum NoRemoteChoice
{
    /// <summary>Create a repository on the hosting service (GitHub) and push there.</summary>
    Publish,

    /// <summary>Add an existing remote by URL and push there.</summary>
    AddRemote,
}

/// <summary>
/// Push with no remote at all: instead of a dead end, offer the ways forward. With a hosting
/// plugin on, "Publish to GitHub…" is the main button and adding an existing remote by URL is the
/// other; without one, adding a remote is the only (main) choice.
/// </summary>
/// <param name="hostName">The hosting service's name ("GitHub"), or null when no hosting plugin is on.</param>
public sealed partial class NoRemoteDialogViewModel(string branch, string? hostName = null) : DialogViewModelBase
{
    public override string Title => "No Remote to Push To";
    public override string OkText => hostName is null ? "Add a Remote by URL…" : $"Publish to {hostName}…";
    public override bool FitsContent => true;
    public override double Width => 460;
    public override double MinWidth => 380;
    public override double MinHeight => 0;

    public string Message { get; } = hostName is null
        ? $"This repository has no remote yet, so there's nowhere to push {branch}. Add a remote that already exists " +
          "(GitHub, GitLab, a server, a folder) by its URL."
        : $"This repository has no remote yet, so there's nowhere to push {branch}. Create a repository on your {hostName} " +
          "account and push there, or add a remote that already exists (GitHub, GitLab, a server, a folder) by its URL.";

    /// <summary>"Add a remote by URL…" as a second button: only when Publish is the main one.</summary>
    public bool CanAlsoAddRemote => hostName is not null;

    /// <summary>The choice; the main button's unless "Add a remote by URL…" was clicked.</summary>
    public NoRemoteChoice Choice { get; private set; } = hostName is null ? NoRemoteChoice.AddRemote : NoRemoteChoice.Publish;

    /// <summary>The secondary option: picks it and closes the dialog with OK.</summary>
    [RelayCommand]
    private Task AddRemoteAsync()
    {
        Choice = NoRemoteChoice.AddRemote;
        return OkCommand.ExecuteAsync(null);
    }
}
