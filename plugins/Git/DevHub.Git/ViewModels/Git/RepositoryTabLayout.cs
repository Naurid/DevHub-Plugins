using CommunityToolkit.Mvvm.ComponentModel;
using DevHub.Git.Core;
using DevHub.Sdk.Core.Settings;

namespace DevHub.ViewModels.Git;

/// <summary>
/// Panel widths and view choices inside repository tabs. One instance (a DI singleton) is shared
/// by all repository tabs and diff tabs, so switching tabs doesn't make the panels jump and a
/// view switch applies everywhere at once. The widths aren't saved yet; the view choices are.
/// </summary>
public sealed partial class RepositoryTabLayout : ObservableObject
{
    private readonly ISettingsStore? _store;

    /// <param name="store">Where view preferences are saved (null in tests: not saved).</param>
    public RepositoryTabLayout(ISettingsStore? store = null)
    {
        _store = store;
        var git = store?.LoadSection<GitSettings>() ?? new GitSettings();
        _changesAsTree = git.ChangesAsTree;
        _diffSideBySide = git.DiffSideBySide;
    }

    /// <summary>Local Changes shows folders (Fork's tree view) instead of a flat list. Saved.</summary>
    [ObservableProperty] private bool _changesAsTree;

    partial void OnChangesAsTreeChanged(bool value) =>
        _store?.UpdateSection<GitSettings>(s => s.ChangesAsTree = value);

    /// <summary>Every diff (Local Changes, commits, stashes, diff tabs) is side by side instead of unified. Saved.</summary>
    [ObservableProperty] private bool _diffSideBySide;

    partial void OnDiffSideBySideChanged(bool value) =>
        _store?.UpdateSection<GitSettings>(s => s.DiffSideBySide = value);

    /// <summary>The sidebar (header + Local Changes, All Commits, …).</summary>
    [ObservableProperty] private double _sidebarWidth = 240;

    /// <summary>The file lists in Local Changes (the diff pane takes the rest).</summary>
    [ObservableProperty] private double _changesListWidth = 320;

    /// <summary>All Commits: the details area under the commit list (message, files, diff).</summary>
    [ObservableProperty] private double _commitDetailsHeight = 340;

    /// <summary>All Commits: the message + file list column in the details (the diff takes the rest).</summary>
    [ObservableProperty] private double _commitFilesWidth = 320;
}
