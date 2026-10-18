using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Diagrams;
using DevHub.Diagrams.Core;
using DevHub.Integration;
using DevHub.Sdk.Core.Localization;
using DevHub.Services;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Diagrams;

/// <summary>A kind in the New Diagram dialog.</summary>
public sealed record KindChoiceViewModel(IDiagramKind Kind)
{
    public string Name => Kind.Model.Name;
    public string Description => Kind.Model.Description;
    public string Icon => Kind.IconKind;
    public override string ToString() => Name;
}

/// <summary>"Start from" in the New Diagram dialog: an empty diagram or one of the kind's samples.</summary>
public sealed record SampleChoiceViewModel(DiagramSample? Sample)
{
    public string Name => Sample?.Name ?? L.T("Empty diagram");
    public string Description => Sample?.Description ?? "";
    public override string ToString() => Name;
}

/// <summary>
/// New Diagram: the kind (use case, activity, class, MCD, mood board…), a name, and where: a project's
/// <c>docs/diagrams</c> (linked to it) or any folder. Creates the file right away, so the diagram is saved from the start.
/// </summary>
public sealed partial class NewDiagramDialogViewModel : DialogViewModelBase
{
    private readonly DiagramsService _service;
    private readonly IDialogService _dialogs;

    public NewDiagramDialogViewModel(DiagramsService service, IDialogService dialogs, string? projectFolder, string? kindId)
    {
        _service = service;
        _dialogs = dialogs;
        foreach (var kind in service.Kinds)
            Kinds.Add(new KindChoiceViewModel(kind));
        _kind = Kinds.FirstOrDefault(k => k.Kind.Model.Id == kindId) ?? Kinds.FirstOrDefault();
        if (service.Catalog is { } catalog)
            foreach (var p in catalog.Projects)
                Projects.Add(p);
        _project = projectFolder is null ? null
            : Projects.FirstOrDefault(p => SameFolder(p.Folder, projectFolder)) ?? new ProjectInfo(Path.GetFileName(projectFolder), projectFolder);
        if (_project is not null && !Projects.Contains(_project))
            Projects.Insert(0, _project);
        _linkToProject = _project is not null;
        _folder = DefaultFolder();
        _name = DefaultName();
        RefreshSamples();
    }

    public override string Title => L.T("New Diagram");
    public override string OkText => L.T("Create");
    public override double Width => 640;
    public override double Height => 560;

    public ObservableCollection<KindChoiceViewModel> Kinds { get; } = [];
    public ObservableCollection<ProjectInfo> Projects { get; } = [];
    public ObservableCollection<SampleChoiceViewModel> Samples { get; } = [];
    public bool HasSamples => Samples.Count > 1;
    public bool HasProjects => Projects.Count > 0;

    [ObservableProperty] private KindChoiceViewModel? _kind;
    [ObservableProperty] private SampleChoiceViewModel? _sample;
    [ObservableProperty] private string _name;
    [ObservableProperty] private ProjectInfo? _project;
    [ObservableProperty] private bool _linkToProject;
    [ObservableProperty] private string _folder;
    [ObservableProperty] private string? _error;

    /// <summary>The file made, after OK.</summary>
    public string? CreatedPath { get; private set; }

    private bool _folderTouched;

    partial void OnKindChanged(KindChoiceViewModel? value)
    {
        RefreshSamples();
        if (Name.Length == 0 || IsDefaultName())
            Name = DefaultName();
    }

    partial void OnSampleChanged(SampleChoiceViewModel? value)
    {
        if (Name.Length == 0 || IsDefaultName())
            Name = DefaultName();
    }

    /// <summary>The name is one this dialog made up (a kind's or a sample's, maybe numbered): it follows the choices.</summary>
    private bool IsDefaultName() =>
        Kinds.Any(k => Name.StartsWith(k.Name, StringComparison.CurrentCulture))
        || Kinds.SelectMany(k => k.Kind.Model.Samples).Any(s => Name.StartsWith(s.Name, StringComparison.CurrentCulture));

    private void RefreshSamples()
    {
        Samples.Clear();
        Samples.Add(new SampleChoiceViewModel(null));
        foreach (var s in Kind?.Kind.Model.Samples ?? [])
            Samples.Add(new SampleChoiceViewModel(s));
        Sample = Samples[0];
        OnPropertyChanged(nameof(HasSamples));
    }

    partial void OnProjectChanged(ProjectInfo? value)
    {
        LinkToProject = value is not null;
        if (!_folderTouched)
            Folder = DefaultFolder();
    }

    partial void OnLinkToProjectChanged(bool value)
    {
        if (!_folderTouched)
            Folder = DefaultFolder();
    }

    private string DefaultFolder() =>
        LinkToProject && Project is { } p
            ? Path.Combine(p.Folder, ProjectLinks.DiagramsFolder)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), L.T("Diagrams"));

    private string DefaultName()
    {
        var baseName = Sample?.Sample?.Name ?? Kind?.Name ?? L.T("Diagram");
        var name = baseName;
        for (var i = 2; File.Exists(Path.Combine(Folder ?? "", name + DiagramFile.Extension)); i++)
            name = $"{baseName} {i}";
        return name;
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _dialogs.PickFolderAsync(L.T("Where to save the diagram")) is { } folder)
        {
            _folderTouched = true;
            Folder = folder;
        }
    }

    protected override async Task<bool> ApplyChangesAsync()
    {
        Error = null;
        var name = Name.Trim();
        if (Kind is null)
        {
            Error = L.T("Pick a kind of diagram.");
            return false;
        }
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Error = L.T("Give it a name that can be a file name (no \\ / : * ? \" < > |).");
            return false;
        }
        try
        {
            CreatedPath = await _service.CreateFileAsync(Kind.Kind, Folder, name, LinkToProject ? Project?.Folder : null, Sample?.Sample);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Error = e.Message;
            return false;
        }
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Link to Project: one of DevHub's projects, another folder, or none.</summary>
public sealed partial class LinkProjectDialogViewModel : DialogViewModelBase
{
    private readonly IDialogService _dialogs;

    public LinkProjectDialogViewModel(IProjectCatalog? catalog, IDialogService dialogs, string? current)
    {
        _dialogs = dialogs;
        if (catalog is not null)
            foreach (var p in catalog.Projects)
                Projects.Add(p);
        if (current is not null)
            _selected = Projects.FirstOrDefault(p => string.Equals(p.Folder.TrimEnd('\\'), current.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        ?? AddOther(current);
    }

    public override string Title => L.T("Link to Project");
    public override string OkText => L.T("Link");
    public override double Width => 560;
    public override double Height => 460;

    public ObservableCollection<ProjectInfo> Projects { get; } = [];

    [ObservableProperty] private ProjectInfo? _selected;

    /// <summary>The project folder chosen (null = unlinked).</summary>
    public string? Folder => Selected?.Folder;

    private ProjectInfo AddOther(string folder)
    {
        var info = new ProjectInfo(Path.GetFileName(folder.TrimEnd('\\', '/')), folder);
        Projects.Insert(0, info);
        return info;
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _dialogs.PickFolderAsync(L.T("The project's folder")) is { } folder)
            Selected = AddOther(folder);
    }

    [RelayCommand]
    private void Unlink() => Selected = null;
}

/// <summary>Link to Issue: search the boards' issues by key or summary and pick one.</summary>
public sealed partial class LinkIssueDialogViewModel(IIssueLinks issues) : DialogViewModelBase
{
    public override string Title => L.T("Link to Issue");
    public override string OkText => L.T("Link");
    public override double Width => 600;
    public override double Height => 480;

    public ObservableCollection<IssueLinkInfo> Results { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private IssueLinkInfo? _selected;

    partial void OnQueryChanged(string value) => _ = SearchAsync();

    public async Task SearchAsync()
    {
        var query = Query;
        var found = await issues.SearchAsync(query, 50);
        if (query != Query)
            return;   // typed on: a newer search follows
        Results.Clear();
        foreach (var issue in found)
            Results.Add(issue);
        Selected ??= Results.FirstOrDefault();
    }

    protected override bool CanApply() => Selected is not null;
}
