using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Integration;
using DevHub.Sdk.Core.Localization;
using DevHub.Services;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.ToolWindows;

/// <summary>
/// "Diagrams" under a project in the tree (only when the Diagrams plugin is on): the project's diagrams and mood
/// boards, read when the project is first expanded and again when Diagrams says they changed.
/// </summary>
public sealed partial class ProjectDiagramsNode : ObservableObject
{
    private readonly string _projectFolder;
    private readonly IProjectDiagrams _diagrams;
    private readonly IDialogService _dialogs;
    private readonly IStatusService _status;
    private bool _loaded;

    public ProjectDiagramsNode(string projectFolder, IProjectDiagrams diagrams, IDialogService dialogs, IStatusService status)
    {
        _projectFolder = projectFolder;
        _diagrams = diagrams;
        _dialogs = dialogs;
        _status = status;
    }

    public string Name => L.T("Diagrams");
    public string Path => _projectFolder;
    public ObservableCollection<ProjectDiagramNode> Items { get; } = [];
    public string CountText => _loaded ? Items.Count.ToString() : "";

    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>Reads the list the first time (the project row was expanded).</summary>
    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : RefreshAsync();

    /// <summary>Reads the list again, if it was read before (Diagrams changed something).</summary>
    public Task RefreshIfLoadedAsync() => _loaded ? RefreshAsync() : Task.CompletedTask;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IReadOnlyList<DiagramFileInfo> found;
        try
        {
            found = await Task.Run(() => _diagrams.DiagramsOf(_projectFolder));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            found = [];
        }
        Items.Clear();
        foreach (var d in found)
            Items.Add(new ProjectDiagramNode(d, _diagrams, this));
        _loaded = true;
        OnPropertyChanged(nameof(CountText));
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        if (await _diagrams.CreateAsync(_projectFolder) is not null)
            await RefreshAsync();
    }

    internal async Task DeleteAsync(ProjectDiagramNode node)
    {
        var assets = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(node.Path) ?? "", node.Name + ".assets");
        var items = Directory.Exists(assets) ? new[] { node.Path, assets } : [node.Path];
        var confirm = new ConfirmDialogViewModel(L.T($"Delete {node.Name}?"),
            L.T("The diagram file is deleted from the disk (git keeps it if it was committed)."), L.T("Delete"), items);
        if (!await _dialogs.ShowDialogAsync(confirm))
            return;
        try
        {
            File.Delete(node.Path);
            if (Directory.Exists(assets))
                Directory.Delete(assets, recursive: true);
            Items.Remove(node);
            OnPropertyChanged(nameof(CountText));
            _status.Show(L.T($"Deleted {node.Name}."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status.Show(L.T($"{node.Name} couldn't be deleted: {e.Message}"));
        }
    }

    public override string ToString() => Name;
}

/// <summary>A diagram or mood board under a project: double-click opens it.</summary>
public sealed class ProjectDiagramNode
{
    public ProjectDiagramNode(DiagramFileInfo info, IProjectDiagrams diagrams, ProjectDiagramsNode group)
    {
        Info = info;
        OpenCommand = new RelayCommand(() => diagrams.Open(info.Path));
        RevealCommand = new RelayCommand(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{info.Path}\"") { UseShellExecute = true });
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Nothing to open it with: the path is in the tooltip.
            }
        });
        DeleteCommand = new AsyncRelayCommand(() => group.DeleteAsync(this));
    }

    public DiagramFileInfo Info { get; }
    public string Name => Info.Name;
    public string Path => Info.Path;
    public string KindName => Info.KindName;
    public string IconKind => Info.IconKind;

    /// <summary>Leaves never expand; here so the tree's expansion binding finds it on every row.</summary>
    public bool IsExpanded { get; set; }

    public RelayCommand OpenCommand { get; }
    public RelayCommand RevealCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }

    public override string ToString() => Name;
}
