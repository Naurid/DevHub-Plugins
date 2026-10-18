using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Diagrams;
using DevHub.Diagrams.Core;
using DevHub.Sdk.Core.Localization;
using DevHub.Sdk.Core.Settings;
using DevHub.Services;

namespace DevHub.ViewModels.Dialogs.Settings;

/// <summary>Settings → Diagrams: where Save as PNG / SVG puts the images.</summary>
public sealed partial class DiagramsSettingsPageViewModel : SettingsPageViewModelBase
{
    private readonly ISettingsStore _store;
    private readonly IDialogService _dialogs;

    public DiagramsSettingsPageViewModel(ISettingsStore store, IDialogService dialogs)
    {
        _store = store;
        _dialogs = dialogs;
        var settings = store.LoadSection<DiagramsSettings>();
        _place = Places.FirstOrDefault(p => p.Value == settings.ExportPlace.ToString()) ?? Places[0];
        _exportFolder = settings.ExportFolder ?? "";
        _besideSubfolder = settings.BesideSubfolder ?? "";
    }

    public override string Title => L.T("Diagrams");
    public override int Order => 610;
    public override string Keywords => L.T("diagrams export image png svg folder save location");

    public IReadOnlyList<FieldChoice> Places { get; } =
    [
        new(nameof(ExportPlace.Ask), L.T("Ask every time (a save dialog)")),
        new(nameof(ExportPlace.BesideDiagram), L.T("Next to the diagram")),
        new(nameof(ExportPlace.Folder), L.T("Always in one folder")),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolder), nameof(IsBeside), nameof(WhereText))]
    private FieldChoice _place;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhereText))]
    private string _exportFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhereText))]
    private string _besideSubfolder;

    public bool IsFolder => Place.Value == nameof(ExportPlace.Folder);
    public bool IsBeside => Place.Value == nameof(ExportPlace.BesideDiagram);

    /// <summary>Where an image of a diagram called "Login" in C:\Shop\docs\diagrams would go.</summary>
    public string WhereText
    {
        get
        {
            var example = Current().TargetFor(Path.Combine("C:", "Shop", "docs", "diagrams", "Login" + DiagramFile.Extension), "Login", "png");
            return example is null
                ? L.T("Save as PNG and Save as SVG ask where to save each time.")
                : L.T($"Example: Login.png goes to {example} (exporting again replaces it).");
        }
    }

    partial void OnPlaceChanged(FieldChoice value) => MarkModified();
    partial void OnExportFolderChanged(string value) => MarkModified();
    partial void OnBesideSubfolderChanged(string value) => MarkModified();

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _dialogs.PickFolderAsync(L.T("Where diagram images go")) is { } folder)
            ExportFolder = folder;
    }

    private DiagramsSettings Current() => new()
    {
        ExportPlace = Enum.TryParse<ExportPlace>(Place.Value, out var place) ? place : ExportPlace.Ask,
        ExportFolder = string.IsNullOrWhiteSpace(ExportFolder) ? null : ExportFolder.Trim(),
        BesideSubfolder = string.IsNullOrWhiteSpace(BesideSubfolder) ? null : BesideSubfolder.Trim(),
    };

    public override void Apply()
    {
        var current = Current();
        _store.UpdateSection<DiagramsSettings>(s =>
        {
            s.ExportPlace = current.ExportPlace;
            s.ExportFolder = current.ExportFolder;
            s.BesideSubfolder = current.BesideSubfolder;
        });
        IsModified = false;
    }
}
