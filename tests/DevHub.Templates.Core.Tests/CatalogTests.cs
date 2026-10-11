using DevHub.Templates.Core.Browsing;
using DevHub.Templates.Core.Commands;
using DevHub.Templates.Core.Generation;
using Xunit;

namespace DevHub.Templates.Core.Tests;

public class TemplateCatalogTests
{
    private static readonly TemplateLibrary Personal = new("Personal", @"C:\lib", LibrarySource.Personal);
    private static readonly TemplateLibrary BuiltIn = new("Built-in", @"C:\builtin", LibrarySource.BuiltIn);

    private static TemplateEntry T(string name, string language = "C#", string[]? categories = null,
        TemplateKind kind = TemplateKind.Project, TemplateLibrary? library = null, string? description = null) =>
        new(library ?? Personal, @"C:\lib\" + name.ToLowerInvariant(), new TemplateManifest
        {
            Id = name.ToLowerInvariant(), Name = name, Language = language, Categories = categories ?? [],
            Kind = kind, Description = description,
        }, []);

    private static TemplateEntry Broken(string id) => new(Personal, @"C:\lib\" + id, null, ["template.json: bad"]);

    private static string[] Shape(IReadOnlyList<TemplateGroup> groups) =>
        groups.Select(g => g.Name + ": " + string.Join(", ", g.Templates.Select(TemplateCatalog.DisplayName))).ToArray();

    [Fact]
    public void Group_ByCategory_TemplateInEachOfItsCategories_OtherAndProblemsLast()
    {
        var groups = TemplateCatalog.Group(
        [
            T("Unity Game", categories: ["Unity", "Game"]),
            T("Console"),
            T("Godot Game", "GDScript", ["Game"]),
            Broken("oops"),
        ], TemplateGrouping.Category);

        Assert.Equal(["Game: Godot Game, Unity Game", "Unity: Unity Game", "Other: Console", "Problems: oops"], Shape(groups));
    }

    [Fact]
    public void Group_ByLanguage_CaseInsensitiveGroups()
    {
        var groups = TemplateCatalog.Group([T("B", "Rust"), T("A", "rust"), T("C", "C#"), T("D", " ")], TemplateGrouping.Language);

        Assert.Equal(["Any: D", "C#: C", "Rust: A, B"], Shape(groups));
    }

    [Fact]
    public void Filter_EveryWordMustMatchSomewhere()
    {
        TemplateEntry[] all = [T("Unity Game", categories: ["Unity"]), T("Console", description: "A small CLI tool"), T("Web App", "TypeScript")];

        Assert.Equal(["Unity Game"], Names(TemplateCatalog.Filter(all, new TemplateFilter("unity c#"))));
        Assert.Equal(["Console"], Names(TemplateCatalog.Filter(all, new TemplateFilter("cli"))));
        Assert.Equal(["Web App"], Names(TemplateCatalog.Filter(all, new TemplateFilter("  TYPE  "))));
        Assert.Empty(TemplateCatalog.Filter(all, new TemplateFilter("unity web")));
        Assert.Equal(3, TemplateCatalog.Filter(all, new TemplateFilter("")).Count());
    }

    [Fact]
    public void Filter_ByLibraryAndKind_BrokenOnesShowForEveryKind()
    {
        TemplateEntry[] all = [T("P"), T("I", kind: TemplateKind.Item), T("B", library: BuiltIn), Broken("x")];

        Assert.Equal(["P", "B", "x"], Names(TemplateCatalog.Filter(all, new TemplateFilter(Kind: TemplateKind.Project))));
        Assert.Equal(["B"], Names(TemplateCatalog.Filter(all, new TemplateFilter(Library: "Built-in"))));
    }

    private static string[] Names(IEnumerable<TemplateEntry> entries) => entries.Select(TemplateCatalog.DisplayName).ToArray();
}

/// <summary>The templates shipped in BuiltInTemplates/ (copied next to the tests like next to the app).</summary>
public class BuiltInTemplatesTests : GenerationTestBase
{
    private static readonly TemplateLibrary BuiltIn = new("Built-in", TemplateLibraries.BuiltInRoot, LibrarySource.BuiltIn);

    [Fact]
    public async Task AllLoadAndAreValid()
    {
        var templates = await TemplateStore.ListAsync(BuiltIn);

        Assert.Contains(templates, t => t.Id == "dotnet-console");
        Assert.Contains(templates, t => t.Id == "empty-project");
        Assert.All(templates, t => Assert.True(t.IsUsable, t.Id + ": " + string.Join("; ", t.Problems)));
    }

    [Fact]
    public async Task EmptyProject_MakesItsFolders()
    {
        var template = (await TemplateStore.ListAsync(BuiltIn)).Single(t => t.Id == "empty-project");
        var parent = Dir("projects");

        var staged = await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "Notes")), parent);
        await staged.Staged!.PlaceAsync();

        var root = Path.Combine(parent, "Notes");
        Assert.Equal(["README.md"], Files(root));
        Assert.True(Directory.Exists(Path.Combine(root, "src")) && Directory.Exists(Path.Combine(root, "docs")));
        Assert.StartsWith("# Notes\n\nCreated by Tester on 2026-10-11.", Read(Path.Combine(root, "README.md")));
    }

    [Fact]
    public async Task DotnetConsole_RealDotnet_ProjectEndsUpInSrc()
    {
        var template = (await TemplateStore.ListAsync(BuiltIn)).Single(t => t.Id == "dotnet-console");
        var parent = Dir("projects");

        var staged = await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "Hello")), parent);
        Assert.True(staged.Succeeded, string.Join("; ", staged.Errors));
        await staged.Staged!.PlaceAsync();

        Assert.Equal([".gitignore", "README.md", "src/Hello/Hello.csproj", "src/Hello/Program.cs"], Files(Path.Combine(parent, "Hello")));
    }
}
