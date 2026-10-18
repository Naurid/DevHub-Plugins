using DevHub.Controls;
using DevHub.Diagrams.Core;
using DevHub.Diagrams.Rendering;
using DevHub.Integration;
using DevHub.Sdk.Core.Localization;
using DevHub.Services;
using DevHub.ViewModels.Diagrams;
using DevHub.ViewModels.Documents;

namespace DevHub.Diagrams;

/// <summary>
/// The diagram kinds that are on (from the kind plugins), opening diagram files as tabs, creating new ones, and the
/// <see cref="IProjectDiagrams"/> hook other plugins use to list a project's diagrams.
/// </summary>
public sealed class DiagramsService(IEnumerable<IDiagramKind> kinds, IServiceProvider services, IDialogService dialogs,
    IStatusService status) : IProjectDiagrams
{
    /// <summary>The kinds, in the New Diagram dialog's order.</summary>
    public IReadOnlyList<IDiagramKind> Kinds { get; } = kinds.OrderBy(k => k.Order).ThenBy(k => k.Model.Name).ToList();

    // Resolved on use: the document service is built from the documents and actions, which may need this service.
    private IDocumentService documents => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IDocumentService>(services);

    public IDiagramKind? Kind(string id) => Kinds.FirstOrDefault(k => k.Model.Id == id);

    // Also on use: Projects offers the catalog and uses this service's hook in turn.
    public IProjectCatalog? Catalog => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<IProjectCatalog>(services);

    public event EventHandler? Changed;

    /// <summary>Tells lists showing diagrams to refresh (a diagram was created, saved with a new link, deleted).</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public static string IdFor(string path) => DiagramDocumentFactory.Prefix + Path.GetFullPath(path);

    public void Open(string path)
    {
        if (!documents.OpenById(IdFor(path)))
            status.Show(L.T($"{Path.GetFileName(path)} couldn't be opened."));
    }

    /// <summary>Asks for a diagram file, then opens it.</summary>
    public async Task OpenFromDiskAsync()
    {
        var path = await dialogs.PickFileAsync(L.T("Open Diagram"), L.T("Diagrams and mood boards"), ["*" + DiagramFile.Extension]);
        if (path is not null)
            Open(path);
    }

    /// <summary>The project of the tab in front (a repository tab, or a diagram linked to a project), or null.</summary>
    public string? ActiveProject =>
        documents.Active switch
        {
            DiagramDocumentViewModel d => d.ProjectFolder,
            { ContextPath: { } folder } => folder,
            _ => null,
        };

    public Task<string?> CreateAsync(string projectFolder) => CreateAsync(projectFolder, null);

    /// <summary>The New Diagram dialog; creates the file, opens it and returns its path (null when cancelled).</summary>
    public async Task<string?> CreateAsync(string? projectFolder, string? kindId)
    {
        var dialog = new NewDiagramDialogViewModel(this, dialogs, projectFolder ?? ActiveProject, kindId);
        if (!await dialogs.ShowDialogAsync(dialog) || dialog.CreatedPath is not { } path)
            return null;
        RaiseChanged();
        Open(path);
        return path;
    }

    /// <summary>Writes a new diagram of a kind (refuses to overwrite an existing file).</summary>
    public async Task<string> CreateFileAsync(IDiagramKind kind, string folder, string name, string? projectFolder, DiagramSample? sample = null)
    {
        var path = Path.Combine(folder, name + DiagramFile.Extension);
        if (File.Exists(path))
            throw new IOException(L.T($"{Path.GetFileName(path)} already exists."));
        var diagram = sample?.Create() ?? kind.Model.Create(name);
        if (sample is not null)
        {
            diagram.Title = name;
            MeasureSurface.FitAll(diagram, kind);
            sample.Arrange?.Invoke(diagram).Apply(diagram);
        }
        if (projectFolder is not null)
            diagram.Project = ProjectLinks.Relative(path, projectFolder);
        await DiagramFile.SaveAsync(diagram, path);
        return path;
    }

    public IReadOnlyList<DiagramFileInfo> DiagramsOf(string projectFolder)
    {
        var files = ProjectLinks.FilesUnder(projectFolder);
        return files.Select(Info).OfType<DiagramFileInfo>().OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public IReadOnlyList<DiagramFileInfo> Mentioning(string folder, string issueKey)
    {
        var result = new List<DiagramFileInfo>();
        foreach (var path in ProjectLinks.FilesUnder(folder))
        {
            try
            {
                var diagram = DiagramFile.Read(File.ReadAllText(path));
                if (diagram.Nodes.Any(n => string.Equals(n.Prop("issue"), issueKey, StringComparison.OrdinalIgnoreCase)) && Info(path) is { } info)
                    result.Add(info);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DiagramFileException)
            {
                // Unreadable diagrams aren't listed.
            }
        }
        return result;
    }

    /// <summary>A file's name and kind for lists, or null when it isn't a readable diagram.</summary>
    public DiagramFileInfo? Info(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var json = System.Text.Json.JsonDocument.Parse(stream);
            var kindId = json.RootElement.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
            var kind = Kind(kindId);
            return new DiagramFileInfo(path, DiagramFile.NameOf(path), kind?.Model.Name ?? L.T("Diagram"), kind?.IconKind ?? IconKind.Template);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>Opens <c>Diagram:&lt;path&gt;</c> tabs (and restores them at the next start).</summary>
public sealed class DiagramDocumentFactory(IServiceProvider services) : IDocumentFactory
{
    public const string Prefix = "Diagram:";

    public string IdPrefix => Prefix;

    public DocumentViewModelBase? Create(string id)
    {
        var path = id[Prefix.Length..];
        if (!File.Exists(path))
            return null;
        return Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<DiagramDocumentViewModel>(services, path);
    }
}
