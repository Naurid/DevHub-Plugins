using System.Text;
using DevHub.Templates.Core.Rendering;

namespace DevHub.Templates.Core.Tests;

/// <summary>A temp folder per test, helpers to build templates and fake tools (.cmd scripts) in it.</summary>
public abstract class GenerationTestBase : IDisposable
{
    protected readonly string Root = Directory.CreateTempSubdirectory("devhub-gen-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    protected string Dir(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    protected static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    protected static string Read(string path) => File.ReadAllText(path);

    /// <summary>A .cmd script in tools/ (a stand-in for dotnet / cargo / npm): returns its full path.</summary>
    protected string Tool(string name, params string[] lines)
    {
        var path = Path.Combine(Dir("tools"), name + ".cmd");
        File.WriteAllText(path, "@echo off\r\n" + string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);
        return path;
    }

    /// <summary>A template on disk: template.json + content files (relative path → text).</summary>
    protected async Task<TemplateEntry> Template(TemplateManifest manifest, params (string Path, string Text)[] content)
    {
        var library = new TemplateLibrary("Personal", Path.Combine(Root, "library"), LibrarySource.Personal);
        var created = await TemplateStore.CreateAsync(library, manifest);
        Xunit.Assert.True(created.Succeeded, string.Join("; ", created.Errors));
        foreach (var (path, text) in content)
            Write(Path.Combine(created.Entry!.ContentFolder, path.Replace('/', '\\')), text);
        return created.Entry!;
    }

    protected static RenderContext Context(params (string Name, string Value)[] values) =>
        new(values.ToDictionary(v => v.Name, v => v.Value), "Tester", new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero));

    protected static IEnumerable<string> StagingFolders(string folder) =>
        Directory.EnumerateDirectories(folder, ".devhub-staging-*");

    /// <summary>Every file under a folder, as sorted "/"-separated relative paths.</summary>
    protected static string[] Files(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
}
