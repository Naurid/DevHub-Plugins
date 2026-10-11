using System.Diagnostics;
using DevHub.Sdk.Core.Settings;
using DevHub.Templates.Core;
using DevHub.Templates.Core.Browsing;
using DevHub.Templates.Core.Commands;
using DevHub.Templates.Core.Generation;
using DevHub.Templates.Core.Rendering;

namespace DevHub.Services;

/// <summary>
/// What the Templates screens share: the libraries in use (from the plugin's settings), the
/// command runner and generator, and the remembered choices (author, last folder, grouping).
/// </summary>
public sealed class TemplatesService(ISettingsStore store)
{
    public CommandRunner Runner { get; } = new();

    public ProjectGenerator Generator => new(Runner);

    /// <summary>
    /// The base folder for the personal library: the DEVHUB_DATA_DIR override (like the settings),
    /// else the roaming AppData folder.
    /// </summary>
    private static string AppDataRoot =>
        Environment.GetEnvironmentVariable("DEVHUB_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private TemplatesSettings Settings => store.LoadSection<TemplatesSettings>();

    /// <summary>Personal first, then the extra folders, then the built-ins.</summary>
    public IReadOnlyList<TemplateLibrary> Libraries => TemplateLibraries.Resolve(Settings, AppDataRoot, TemplateLibraries.BuiltInRoot);

    public TemplateLibrary PersonalLibrary => Libraries[0];

    public Task<IReadOnlyList<TemplateEntry>> LoadAllAsync(CancellationToken ct = default) =>
        TemplateCatalog.LoadAllAsync(Libraries, ct);

    /// <summary>The <c>{{Author}}</c> value: from settings, else the Windows user name.</summary>
    public string Author => Settings.Author is { Length: > 0 } author ? author : Environment.UserName;

    /// <summary>A fresh render context for one generation (its own GUIDs and time).</summary>
    public RenderContext NewContext(IReadOnlyDictionary<string, string> values) => new(values, Author, DateTimeOffset.Now);

    public string? LastLocation
    {
        get => Settings.LastLocation;
        set => store.UpdateSection<TemplatesSettings>(s => s.LastLocation = value);
    }

    public bool GroupByLanguage
    {
        get => Settings.GroupByLanguage;
        set => store.UpdateSection<TemplatesSettings>(s => s.GroupByLanguage = value);
    }

    /// <summary>Opens a folder in Explorer (created first, e.g. the personal library before its first template).</summary>
    public static void Reveal(string folder)
    {
        Directory.CreateDirectory(folder);
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add(folder);
        Process.Start(info)?.Dispose();
    }
}
