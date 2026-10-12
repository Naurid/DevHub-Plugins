using DevHub.Templates.Core.Editing;
using Xunit;

namespace DevHub.Templates.Core.Tests;

public class TemplateIdsTests : GenerationTestBase
{
    [Theory]
    [InlineData("Unity 3D Game!", "unity-3d-game")]
    [InlineData("MyHTTPServer", "my-httpserver")]
    [InlineData("MyGame", "my-game")]
    [InlineData("C# Console", "c-console")]
    [InlineData("  --  ", "template")]
    [InlineData("Ünïcode", "n-code")]
    public void FromName_IsAValidId(string name, string id)
    {
        Assert.Equal(id, TemplateIds.FromName(name));
        Assert.Empty(ManifestValidator.Validate(NewTemplate.Create(name.Trim() is { Length: > 0 } n ? n : "x", TemplateIds.FromName(name),
            TemplateKind.Project, "C#")));
    }

    [Fact]
    public void Unique_AddsANumberWhenTaken()
    {
        var library = new TemplateLibrary("Personal", Root, LibrarySource.Personal);
        Dir("game");
        Dir("game-2");

        Assert.Equal("game-3", TemplateIds.Unique(library, "game"));
        Assert.Equal("other", TemplateIds.Unique(library, "other"));
    }
}

public class ListTextTests
{
    [Fact]
    public void SplitList_CommasOrLines_TrimmedWithoutDuplicates() =>
        Assert.Equal(["Console", ".NET", "Game"], ListText.SplitList(" Console, .NET ,\r\nGame,, console"));

    [Fact]
    public void SplitLines_KeepsSpacesInsideAnArgument() =>
        Assert.Equal(["new", "console", "--name", "My App"], ListText.SplitLines("new\r\nconsole\n\n--name\nMy App\n"));

    [Fact]
    public void JoinLines_RoundTrips() =>
        Assert.Equal(["a b", "c"], ListText.SplitLines(ListText.JoinLines(["a b", "c"])));
}

public class NewTemplateAndManifestTextTests
{
    [Fact]
    public void Create_IsValidAndRoundTripsThroughJson()
    {
        var manifest = NewTemplate.Create("Web App", "web-app", TemplateKind.Item, " ");

        Assert.Empty(ManifestValidator.Validate(manifest));
        Assert.Equal("Any", manifest.Language);
        var (parsed, problems) = ManifestText.Parse(ManifestSerializer.Write(manifest));
        Assert.Empty(problems);
        Assert.Equal(TemplateKind.Item, parsed!.Kind);
    }

    [Fact]
    public void Parse_BrokenJson_SaysWhy()
    {
        var (manifest, problems) = ManifestText.Parse("{ \"id\": ");

        Assert.Null(manifest);
        Assert.Single(problems);
    }

    [Fact]
    public void Parse_InvalidManifest_KeepsItWithItsProblems()
    {
        var (manifest, problems) = ManifestText.Parse("""{ "schemaVersion": 1, "id": "Bad Id", "name": "x" }""");

        Assert.NotNull(manifest);
        Assert.Contains(problems, p => p.StartsWith("id", StringComparison.Ordinal));
    }
}

public class TemplateContentTests : GenerationTestBase
{
    private async Task<TemplateEntry> Entry(LibrarySource source = LibrarySource.Personal)
    {
        var created = await Template(NewTemplate.Create("T", "t", TemplateKind.Project, "C#"), ("src/{{Name}}.cs", "class"), ("README.md", "#"));
        return created with { Library = created.Library with { Source = source } };
    }

    private static string[] Shape(TemplateEntry entry) =>
        TemplateContent.List(entry).Select(i => i.IsFolder ? i.Path + "/" : i.Path).ToArray();

    [Fact]
    public async Task List_FoldersBeforeTheirFiles() =>
        Assert.Equal(["README.md", "src/", "src/{{Name}}.cs"], Shape(await Entry()));

    [Fact]
    public async Task CreateRenameDelete()
    {
        var entry = await Entry();

        Assert.Null(TemplateContent.CreateFolder(entry, "src", "Models"));
        Assert.Null(TemplateContent.CreateFile(entry, "src/Models", " {{Name}}Model.cs "));
        Assert.Null(TemplateContent.Rename(entry, "README.md", "readme.md"));
        Assert.Null(TemplateContent.Rename(entry, "src/Models", "models"));
        Assert.Null(TemplateContent.Delete(entry, "src/{{Name}}.cs"));

        Assert.Equal(["readme.md", "src/", "src/models/", "src/models/{{Name}}Model.cs"], Shape(entry));
    }

    [Theory]
    [InlineData("", "a.txt", "already exists")]
    [InlineData("", "{{Name:kebab}}.md", "with a dot")]
    [InlineData("", "con.txt", "reserves")]
    [InlineData("..", "x.txt", "isn't inside")]
    [InlineData("", "  ", "Enter a name")]
    public async Task Create_Problems(string parent, string name, string error)
    {
        var entry = await Entry();
        TemplateContent.CreateFile(entry, "", "a.txt");

        Assert.Contains(error, TemplateContent.CreateFile(entry, parent, name));
    }

    [Fact]
    public async Task Rename_OntoAnotherItem_OrOutside_IsRefused()
    {
        var entry = await Entry();

        Assert.Contains("already exists", TemplateContent.Rename(entry, "README.md", "src"));
        Assert.Contains("doesn't exist", TemplateContent.Rename(entry, "../template.json", "x.json"));
        Assert.Contains("isn't inside", TemplateContent.Delete(entry, "../template.json"));
        Assert.True(File.Exists(Path.Combine(entry.Folder, "template.json")));
    }

    [Fact]
    public async Task ReadOnlyLibrary_ChangesNothing()
    {
        var entry = await Entry(LibrarySource.BuiltIn);

        Assert.Contains("read-only", TemplateContent.CreateFile(entry, "", "new.txt"));
        Assert.Contains("read-only", TemplateContent.Delete(entry, "README.md"));
        Assert.Contains("read-only", TemplateContent.Rename(entry, "README.md", "x.md"));
        Assert.Equal(["README.md", "src/", "src/{{Name}}.cs"], Shape(entry));
    }
}
