using System.IO.Compression;
using DevHub.Sdk.Core.Settings;
using DevHub.Templates.Core.Editing;
using Xunit;

namespace DevHub.Templates.Core.Tests;

public class ProjectFoldersTests
{
    private static TemplateManifest M(string language, params string[] categories) =>
        new() { Id = "t", Name = "T", Language = language, Categories = categories };

    private static TemplatesSettings Settings() => new()
    {
        DefaultProjectFolder = @"C:\Dev",
        LastLocation = @"C:\Last",
        ProjectFolderRules =
        [
            new ProjectFolderRule { Language = "C#", Folder = @"C:\Dev\dotnet" },
            new ProjectFolderRule { Category = "Unity", Folder = @"D:\Unity" },
            new ProjectFolderRule { Language = "C#", Category = "Game", Folder = @"D:\Games\cs" },
            new ProjectFolderRule { Language = "Rust", Folder = "" }, // no folder: ignored
            new ProjectFolderRule { Language = "Any", Category = " ", Folder = @"C:\Nope" }, // matches nothing specific: ignored
        ],
    };

    [Theory]
    [InlineData("C#", new string[0], @"C:\Dev\dotnet")]
    [InlineData("c#", new[] { "Unity" }, @"D:\Unity")] // category beats language
    [InlineData("C#", new[] { "Unity", "game" }, @"D:\Games\cs")] // both beat one
    [InlineData("Rust", new string[0], @"C:\Dev")] // rule without a folder: the default
    [InlineData("Go", new[] { "Web" }, @"C:\Dev")]
    public void For_MostSpecificRule_ThenTheDefault(string language, string[] categories, string expected) =>
        Assert.Equal(expected, ProjectFolders.For(Settings(), M(language, categories)));

    [Fact]
    public void For_NoDefault_TheLastFolder_ElseNull()
    {
        Assert.Equal(@"C:\Last", ProjectFolders.For(new TemplatesSettings { LastLocation = @"C:\Last" }, M("Go")));
        Assert.Null(ProjectFolders.For(new TemplatesSettings(), M("Go")));
    }

    [Fact]
    public void Remember_NewestFirst_OncePerFolder_Capped()
    {
        var settings = new TemplatesSettings();
        for (var i = 0; i < 12; i++)
            ProjectFolders.Remember(settings, new RecentProject($@"C:\p{i}", "Built-in/x", DateTimeOffset.Now));
        ProjectFolders.Remember(settings, new RecentProject(@"C:\P5", "Built-in/y", DateTimeOffset.Now));

        Assert.Equal(ProjectFolders.MaxRecent, settings.RecentProjects.Count);
        Assert.Equal(@"C:\P5", settings.RecentProjects[0].Path);
        Assert.Single(settings.RecentProjects, r => r.Path.Equals(@"C:\p5", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory("devhub-settings-").FullName;
        try
        {
            var store = new JsonSettingsStore(dir);
            store.UpdateSection<TemplatesSettings>(s =>
            {
                s.DefaultProjectFolder = @"C:\Dev";
                s.ProjectFolderRules.Add(new ProjectFolderRule { Category = "Unity", Folder = @"D:\Unity" });
                ProjectFolders.Remember(s, new RecentProject(@"C:\Dev\Game", "Personal/unity", new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero)));
            });

            var loaded = new JsonSettingsStore(dir).LoadSection<TemplatesSettings>();

            Assert.Equal(@"C:\Dev", loaded.DefaultProjectFolder);
            Assert.Equal(@"D:\Unity", loaded.ProjectFolderRules.Single().Folder);
            Assert.Equal("Personal/unity", loaded.RecentProjects.Single().Template);
            Assert.Equal(2026, loaded.RecentProjects.Single().Created.Year);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class TemplatePackageTests : GenerationTestBase
{
    private TemplateLibrary Library(string name) => new(name, Path.Combine(Root, name), LibrarySource.Personal);

    [Fact]
    public async Task ExportThenImport_SameTemplate_FreeIdWhenTaken()
    {
        var source = Library("a");
        var created = await TemplateStore.CreateAsync(source, NewTemplate.Create("Game", "game", TemplateKind.Project, "C#"));
        Write(Path.Combine(created.Entry!.ContentFolder, "src", "{{Name}}.cs"), "class {{Name}} {}");
        var zip = Path.Combine(Root, "game.zip");

        await TemplatePackage.ExportAsync(created.Entry, zip);
        var first = await TemplatePackage.ImportAsync(zip, Library("b"));
        var second = await TemplatePackage.ImportAsync(zip, Library("b"));

        Assert.True(first.Succeeded, string.Join("; ", first.Errors));
        Assert.Equal("game", first.Entry!.Id);
        Assert.Equal("class {{Name}} {}", Read(Path.Combine(first.Entry.ContentFolder, "src", "{{Name}}.cs")));
        Assert.Equal("game-2", second.Entry!.Id);
        Assert.Equal("game-2", second.Entry.Manifest!.Id);
        Assert.True(second.Entry.IsUsable);
    }

    [Fact]
    public async Task Import_ManifestAtTheTop_Works()
    {
        var zip = Path.Combine(Root, "flat.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Entry(archive, "template.json", ManifestSerializer.Write(NewTemplate.Create("Flat", "flat", TemplateKind.Item, "Any")));
            Entry(archive, "content/a.txt", "a");
        }

        var result = await TemplatePackage.ImportAsync(zip, Library("lib"));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.True(File.Exists(Path.Combine(result.Entry!.ContentFolder, "a.txt")));
    }

    [Fact]
    public async Task Import_ZipSlip_IsRefused_AndNothingStays()
    {
        var zip = Path.Combine(Root, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Entry(archive, "evil/template.json", ManifestSerializer.Write(NewTemplate.Create("Evil", "evil", TemplateKind.Item, "Any")));
            Entry(archive, "evil/../../outside.txt", "gotcha");
        }
        var library = Library("lib");

        var result = await TemplatePackage.ImportAsync(zip, library);

        Assert.Contains("outside the template", result.Errors[0]);
        Assert.False(File.Exists(Path.Combine(Root, "outside.txt")));
        Assert.False(Directory.Exists(Path.Combine(library.Root, "evil")));
    }

    [Fact]
    public async Task Import_NotATemplate_SaysSo()
    {
        var zip = Path.Combine(Root, "other.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            Entry(archive, "readme.txt", "hi");

        Assert.Contains("doesn't hold one template", (await TemplatePackage.ImportAsync(zip, Library("lib"))).Errors[0]);
    }

    private static void Entry(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(text);
    }
}
