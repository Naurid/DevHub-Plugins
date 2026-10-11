using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Controls;
using DevHub.Integration;
using DevHub.Projects.Core;
using DevHub.Sdk.Core.Search;
using DevHub.Sdk.Core.Settings;
using DevHub.Services;
using DevHub.ViewModels.Documents;

namespace DevHub.ViewModels.ToolWindows;

/// <summary>
/// The Repositories tool window (left side, Alt+1): the folders you chose to scan, each with
/// the git repositories found under it, as a tree. All file-system work lives in
/// <see cref="IRepositoryScanner"/> (Core); this drives it and shapes the results for the tree.
/// </summary>
public partial class RepositoriesViewModel : ToolWindowViewModelBase, IProjectFolders
{
    private readonly IRepositoryScanner _scanner;
    private readonly IDialogService _dialogs;
    private readonly IProjectLauncher _launcher;
    private readonly IStatusService _status;
    private readonly ISettingsStore _store;
    private readonly IProjectOpener? _opener;
    private readonly IAddFilesFromTemplate? _addFiles;

    /// <summary>Results of the last scan (flat). The tree is built from this + the folders.</summary>
    private IReadOnlyList<GitRepository> _repositories = [];

    public override string Id => "repositories";
    public override string Title => "Projects";
    public override string IconKind => Controls.IconKind.Folder;
    public override ToolWindowSlot Slot => ToolWindowSlot.Left;

    /// <summary>First in the stripe and Alt+1, like JetBrains' Project; also offered on the Welcome view.</summary>
    public override int Order => 0;
    public override int? WelcomeOrder => 50;

    /// <summary>Folders the user has chosen to scan; saved in settings.</summary>
    public ObservableCollection<string> ScanFolders { get; } = new();

    /// <summary>Tree roots: one node per scan folder, repositories as children.</summary>
    public ObservableCollection<ScanFolderNode> Roots { get; } = new();

    /// <summary>True when at least one folder is designated (drives the empty state).</summary>
    public bool HasFolders => ScanFolders.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isScanning;

    /// <summary>Selected tree row (a <see cref="ScanFolderNode"/> or a <see cref="RepositoryNode"/>), two-way bound.</summary>
    [ObservableProperty] private object? _selectedNode;

    /// <summary>Search box text; filters repositories by fuzzy name match.</summary>
    [ObservableProperty] private string _filter = "";

    public RepositoriesViewModel(IRepositoryScanner scanner, IDialogService dialogs, IProjectLauncher launcher,
        IStatusService status, ISettingsStore store, IEnumerable<IProjectOpener> openers, IEnumerable<IAddFilesFromTemplate> addFiles)
    {
        _scanner = scanner;
        _dialogs = dialogs;
        _launcher = launcher;
        _status = status;
        _store = store;
        // Soft hook: another plugin (Git) may open projects inside DevHub. None → the external tool.
        _opener = openers.FirstOrDefault();
        _addFiles = addFiles.FirstOrDefault();

        foreach (var folder in _store.LoadSection<ProjectsSettings>().RepositoryFolders)
            ScanFolders.Add(folder);
        RebuildTree();

        // Adding/removing a folder: Scan's CanExecute, the empty state, the tree and settings change.
        ScanFolders.CollectionChanged += (_, _) =>
        {
            ScanCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasFolders));
            RebuildTree();
            var folders = ScanFolders.ToList();
            _store.UpdateSection<ProjectsSettings>(s => s.RepositoryFolders = folders);
        };

        // Show the repositories of the saved folders straight away (the scan runs on a background
        // thread; the tree fills in when it finishes). F5 / Rescan picks up later changes.
        if (ScanFolders.Count > 0)
            ScanCommand.Execute(null);
    }

    // ---- IProjectFolders (offered to other plugins, e.g. Git's Clone) ----

    IReadOnlyList<string> IProjectFolders.Folders => ScanFolders.ToList();

    Task IProjectFolders.RescanAsync() => ScanCommand.CanExecute(null) ? ScanCommand.ExecuteAsync(null) : Task.CompletedTask;

    partial void OnFilterChanged(string value) => RebuildTree();

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Choose a folder to scan");
        if (!string.IsNullOrEmpty(folder) &&
            !ScanFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            ScanFolders.Add(folder);

            // A new folder is only useful once its repositories are found.
            if (ScanCommand.CanExecute(null))
                await ScanCommand.ExecuteAsync(null);
        }
    }

    private bool CanScan() => !IsScanning && ScanFolders.Count > 0;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        IsScanning = true;
        _status.Show("Scanning…");
        try
        {
            _repositories = await _scanner.ScanAsync(ScanFolders.ToList());
            RebuildTree();

            _status.Show(_repositories.Count switch
            {
                0 => "No git repositories found.",
                1 => "Found 1 repository.",
                _ => $"Found {_repositories.Count} repositories."
            });
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void RemoveFolder(string folder) => ScanFolders.Remove(folder);

    /// <summary>
    /// Opens the project inside DevHub through the plugin that offers it (Git: its repository tab),
    /// or, with no such plugin, in the external tool chosen for its language.
    /// </summary>
    private void OpenInDevHub(GitRepository repo)
    {
        if (_opener is null || !_opener.Open(repo.Path))
            Open(repo);
    }

    /// <summary>
    /// Enter in the tree: open the selected repository as a tab. (A key binding on the TreeView,
    /// because key events start at the focused row and bubble up, never into the row's content.)
    /// </summary>
    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedNode is RepositoryNode node)
            OpenInDevHub(node.Repository);
    }

    private void Open(GitRepository repo)
    {
        try
        {
            _launcher.Open(repo);
            _status.Show($"Opened {repo.Name}.");
        }
        catch (Exception ex)
        {
            _status.Show($"Couldn't open {repo.Name}: {ex.Message}");
        }
    }

    private void Reveal(GitRepository repo)
    {
        try
        {
            _launcher.RevealInFileManager(repo);
        }
        catch (Exception ex)
        {
            _status.Show($"Couldn't reveal {repo.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Rebuilds the tree from the folders, the last scan and the filter. Each repository goes
    /// under the (longest) scan folder that contains it. With a filter, only fuzzy matches are
    /// shown, best first, and folders without matches are hidden.
    /// </summary>
    private void RebuildTree()
    {
        Roots.Clear();
        var filtering = !string.IsNullOrWhiteSpace(Filter);

        foreach (var folder in ScanFolders)
        {
            var repos = _repositories
                .Where(r => OwningFolder(r.Path) == folder)
                .Select(r => (Repo: r, Match: filtering ? FuzzyMatcher.Match(Filter, r.Name) : null))
                .Where(x => !filtering || x.Match is not null)
                .OrderByDescending(x => x.Match?.Score ?? 0)
                .ThenBy(x => x.Repo.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new RepositoryNode(x.Repo, OpenInDevHub, Open, Reveal, _addFiles is null ? null : r => _addFiles.Open(r.Path)))
                .ToList();

            if (filtering && repos.Count == 0)
                continue;

            Roots.Add(new ScanFolderNode(folder, repos, RemoveFolder));
        }
    }

    private string? OwningFolder(string repoPath) =>
        ScanFolders
            .Where(f => repoPath.StartsWith(f.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();
}

/// <summary>A scan folder in the tree (expanded by default).</summary>
public sealed partial class ScanFolderNode : ObservableObject
{
    public ScanFolderNode(string path, IReadOnlyList<RepositoryNode> repositories, Action<string> remove)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;
        Repositories = repositories;
        RemoveCommand = new RelayCommand(() => remove(Path));
    }

    public string Path { get; }
    public string Name { get; }
    public IReadOnlyList<RepositoryNode> Repositories { get; }
    public string CountText => Repositories.Count.ToString();
    public RelayCommand RemoveCommand { get; }

    [ObservableProperty] private bool _isExpanded = true;

    public override string ToString() => Name;
}

/// <summary>A repository leaf in the tree. Carries its own commands so the context menu can bind directly.</summary>
public sealed class RepositoryNode
{
    /// <param name="addFiles">Templates' Add Files (null when that plugin is off: the menu item hides).</param>
    public RepositoryNode(GitRepository repository, Action<GitRepository> openInDevHub,
        Action<GitRepository> open, Action<GitRepository> reveal, Action<GitRepository>? addFiles = null)
    {
        CanAddFiles = addFiles is not null;
        AddFilesCommand = new RelayCommand(() => addFiles?.Invoke(repository));
        Repository = repository;
        OpenInDevHubCommand = new RelayCommand(() => openInDevHub(repository));
        OpenCommand = new RelayCommand(() => open(repository));
        RevealCommand = new RelayCommand(() => reveal(repository));
    }

    public GitRepository Repository { get; }
    public string Name => Repository.Name;
    public string Path => Repository.Path;
    public string Language => Repository.Language.DisplayName;
    public string IconKind => Controls.IconKind.Folder;
    /// <summary>Open as a repository tab (double-click, default menu item).</summary>
    public RelayCommand OpenInDevHubCommand { get; }

    /// <summary>Open in the external tool chosen for its language (Settings → Open With).</summary>
    public RelayCommand OpenCommand { get; }
    public RelayCommand RevealCommand { get; }

    /// <summary>Add Files from Template… (the Templates plugin), into this project.</summary>
    public RelayCommand AddFilesCommand { get; }
    public bool CanAddFiles { get; }

    public override string ToString() => Name;
}
