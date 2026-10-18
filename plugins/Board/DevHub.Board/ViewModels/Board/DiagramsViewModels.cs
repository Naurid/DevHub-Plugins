using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Integration;
using DevHub.Sdk.Core.Localization;
using DevHub.Services.Board;

namespace DevHub.ViewModels.Board;

/// <summary>A diagram in a board's lists: click opens it.</summary>
public sealed class DiagramRow(DiagramFileInfo info, Action<string> open)
{
    public DiagramFileInfo Info { get; } = info;
    public RelayCommand OpenCommand { get; } = new(() => open(info.Path));
    public string Name => Info.Name;
    public string KindName => Info.KindName;
    public string IconKind => Info.IconKind;
    public string Path => Info.Path;
    public override string ToString() => Name;
}

/// <summary>An issue's Diagrams section: the diagrams of the board's project with a shape linked to this issue.</summary>
public sealed class DiagramsSectionProvider(IEnumerable<IProjectDiagrams> diagrams) : IIssueSectionProvider
{
    private readonly IProjectDiagrams? _diagrams = diagrams.FirstOrDefault();

    public int Order => 250;

    public IssueSectionViewModel? Create(BoardContext context, string issueId) =>
        _diagrams is not null && BoardIssueLinks.ProjectFolderOf(context.Location) is { } folder
            ? new DiagramsSectionViewModel(context, issueId, _diagrams, folder)
            : null;
}

public sealed partial class DiagramsSectionViewModel : IssueSectionViewModel
{
    private readonly IProjectDiagrams _diagrams;
    private readonly string _folder;
    private string _key = "";

    public DiagramsSectionViewModel(BoardContext context, string issueId, IProjectDiagrams diagrams, string folder) : base(context, issueId)
    {
        _diagrams = diagrams;
        _folder = folder;
        _diagrams.Changed += OnDiagramsChanged;
        if (context.Snapshot.Issue(issueId) is { } issue)
        {
            _key = issue.Key;
            _ = LoadAsync();
        }
    }

    public override string Title => L.T("Diagrams");

    public ObservableCollection<DiagramRow> Items { get; } = [];

    [ObservableProperty] private bool _isEmpty;

    public override void Update(Issue issue, BoardSnapshot board)
    {
        if (issue.Key == _key)
            return;
        _key = issue.Key;
        _ = LoadAsync();
    }

    private void OnDiagramsChanged(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LoadAsync());

    private async Task LoadAsync()
    {
        var key = _key;
        IReadOnlyList<DiagramFileInfo> found;
        try
        {
            found = await Task.Run(() => _diagrams.Mentioning(_folder, key));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            found = [];
        }
        if (key != _key)
            return;
        Items.Clear();
        foreach (var d in found)
            Items.Add(new DiagramRow(d, _diagrams.Open));
        IsEmpty = Items.Count == 0;
    }

    public override void Dispose() => _diagrams.Changed -= OnDiagramsChanged;
}

/// <summary>The Diagrams page of a board: the diagrams and mood boards of the board's project folder.</summary>
public sealed class DiagramsPageProvider(IEnumerable<IProjectDiagrams> diagrams) : IBoardPageProvider
{
    public const string PageId = "diagrams";
    private readonly IProjectDiagrams? _diagrams = diagrams.FirstOrDefault();

    public string Id => PageId;
    public string Title => L.T("Diagrams");
    public int Order => 800;

    public bool AppliesTo(BoardDefinition definition) => _diagrams is not null;

    public BoardPageViewModel Create(BoardPageHost host) => new DiagramsPageViewModel(host, _diagrams!);
}

public sealed partial class DiagramsPageViewModel : BoardPageViewModel
{
    private readonly IProjectDiagrams _diagrams;
    private readonly List<DiagramRow> _all = [];
    private string _search = "";
    private bool _loaded;

    public DiagramsPageViewModel(BoardPageHost host, IProjectDiagrams diagrams) : base(host)
    {
        _diagrams = diagrams;
        Folder = BoardIssueLinks.ProjectFolderOf(host.Context.Location);
        _diagrams.Changed += OnDiagramsChanged;
    }

    public override string Title => L.T("Diagrams");

    /// <summary>The project folder the board belongs to, or null (a board kept in DevHub's own folder).</summary>
    public string? Folder { get; }

    public bool HasFolder => Folder is not null;
    public bool NoFolder => Folder is null;

    public ObservableCollection<DiagramRow> Items { get; } = [];

    [ObservableProperty] private bool _isEmpty;

    public override void OnShown(bool shown)
    {
        if (shown && !_loaded)
            _ = LoadAsync();
    }

    public override void OnSearch(string text)
    {
        _search = text.Trim();
        Filter();
    }

    private void OnDiagramsChanged(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (_loaded)
            _ = LoadAsync();
    });

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (Folder is null)
            return;
        IReadOnlyList<DiagramFileInfo> found;
        try
        {
            found = await Task.Run(() => _diagrams.DiagramsOf(Folder));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            found = [];
        }
        _all.Clear();
        _all.AddRange(found.Select(d => new DiagramRow(d, _diagrams.Open)));
        _loaded = true;
        Filter();
    }

    private void Filter()
    {
        Items.Clear();
        foreach (var row in _all.Where(r => _search.Length == 0 || r.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase)
                                             || r.KindName.Contains(_search, StringComparison.CurrentCultureIgnoreCase)))
            Items.Add(row);
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        if (Folder is not null && await _diagrams.CreateAsync(Folder) is not null)
            await LoadAsync();
    }

    public override void Dispose() => _diagrams.Changed -= OnDiagramsChanged;
}
