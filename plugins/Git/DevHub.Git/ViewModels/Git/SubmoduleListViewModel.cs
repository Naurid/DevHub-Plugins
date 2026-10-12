using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Git.Core;
using DevHub.Services;

namespace DevHub.ViewModels.Git;

/// <summary>
/// The Submodules section of a repository tab (G8): other repositories checked out in folders
/// of this one, with where each stands. Update Submodules clones the missing ones and checks
/// every one out at the commit this repository records; Open (double-click) opens a submodule
/// as its own repository tab, where it's worked on like any repository.
/// </summary>
public sealed partial class SubmoduleListViewModel : ViewModelBase
{
    private readonly GitRepositoryClient? _client;
    private readonly RemoteOperations _remote;
    private readonly IStatusService _status;
    private readonly Action<string> _openRepository;

    /// <param name="openRepository">Opens a folder as a repository tab.</param>
    public SubmoduleListViewModel(GitRepositoryClient? client, RemoteOperations remote, IStatusService status,
        Action<string> openRepository)
    {
        _client = client;
        _remote = remote;
        _status = status;
        _openRepository = openRepository;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems), nameof(Count))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    private IReadOnlyList<SubmoduleItemViewModel> _items = [];

    public bool HasItems => Items.Count > 0;
    public int Count => Items.Count;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private SubmoduleItemViewModel? _selected;

    /// <summary>Takes the freshly loaded submodules, keeping the selection.</summary>
    public void Update(IReadOnlyList<GitSubmodule> submodules)
    {
        if (submodules.SequenceEqual(Items.Select(i => i.Submodule)))
            return; // nothing changed (most refreshes are file edits)
        var keep = Selected?.Submodule.Path;
        Items = submodules.Select(s => new SubmoduleItemViewModel(s, Open)).ToList();
        Selected = Items.FirstOrDefault(i => i.Submodule.Path == keep);
    }

    private bool CanUpdate() => _client is not null && HasItems;

    /// <summary>Clone missing submodules and check each out at the recorded commit (network: status bar, Cancel).</summary>
    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        if (await _remote.RunNetworkAsync("Updating submodules", (p, ct) => _client!.UpdateSubmodulesAsync(p, ct)))
            _status.Show("Submodules updated: each is checked out at the commit this repository records.");
    }

    private bool CanOpen() => Selected is { CanOpen: true };

    /// <summary>Open the selected submodule as its own repository tab (Enter, the Open button).</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open() => Open(Selected);

    private void Open(SubmoduleItemViewModel? item)
    {
        if (_client is null || item is null)
            return;
        if (!item.CanOpen)
        {
            _status.Show($"{item.Path} isn't cloned yet: Update Submodules first.");
            return;
        }
        _openRepository(_client.SubmoduleFolder(item.Submodule));
    }
}

/// <summary>A submodule row: path, state, commit.</summary>
public sealed class SubmoduleItemViewModel
{
    public SubmoduleItemViewModel(GitSubmodule submodule, Action<SubmoduleItemViewModel> open)
    {
        Submodule = submodule;
        OpenCommand = new RelayCommand(() => open(this));
    }

    public GitSubmodule Submodule { get; }
    public string Path => Submodule.Path;
    public string ShortId => Submodule.ShortId;
    public string? Describe => Submodule.Describe;

    /// <summary>Cloned, so it can be opened (not initialized = an empty folder).</summary>
    public bool CanOpen => Submodule.State != SubmoduleState.NotInitialized;

    public string StateText => Submodule.State switch
    {
        SubmoduleState.NotInitialized => "not cloned",
        SubmoduleState.DifferentCommit => "other commit",
        SubmoduleState.Conflict => "conflict",
        _ => "up to date",
    };

    public bool IsUpToDate => Submodule.State == SubmoduleState.UpToDate;
    public bool IsWarning => Submodule.State is SubmoduleState.NotInitialized or SubmoduleState.DifferentCommit;
    public bool IsError => Submodule.State == SubmoduleState.Conflict;

    public string ToolTip => Submodule.State switch
    {
        SubmoduleState.NotInitialized => "Not cloned yet: Update Submodules fetches it.",
        SubmoduleState.DifferentCommit => $"Checked out at {Submodule.ShortId}, not at the commit this repository records. " +
                                          "Update Submodules moves it back; or commit here to record the new commit.",
        SubmoduleState.Conflict => "The recorded commit is in a merge conflict.",
        _ => $"At {Submodule.ShortId}{(Describe is { } d ? $" ({d})" : "")}, as this repository records.",
    };

    /// <summary>Double-click: open as a repository tab.</summary>
    public RelayCommand OpenCommand { get; }

    public override string ToString() => $"{Path}, {StateText}";
}
