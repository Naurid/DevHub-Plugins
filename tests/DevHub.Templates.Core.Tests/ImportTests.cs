using System.Text;
using DevHub.Templates.Core.Commands;
using DevHub.Templates.Core.Editing;
using DevHub.Templates.Core.Generation;
using DevHub.Templates.Core.Importing;
using Xunit;

namespace DevHub.Templates.Core.Tests;

public class IgnoreRulesTests
{
    private static readonly IgnoreRules Defaults = new(IgnoreRules.Defaults);

    [Theory]
    [InlineData(".git", true, true)]
    [InlineData("src/bin", true, true)]
    [InlineData("src/bin", false, false)] // "bin/" means a folder: a file called bin stays
    [InlineData("Game.csproj.user", false, true)]
    [InlineData("src/Program.cs", false, false)]
    [InlineData("NODE_MODULES", true, true)]
    public void Defaults_MatchByNameAtAnyDepth(string path, bool isFolder, bool ignored) =>
        Assert.Equal(ignored, Defaults.IsIgnored(path, isFolder));

    [Fact]
    public void Anchored_CommentsAndBlanks()
    {
        var rules = new IgnoreRules(["# generated", "", "/out", "src/gen/", "**/*.tmp", @"docs\drafts"]);

        Assert.True(rules.IsIgnored("out", true));
        Assert.False(rules.IsIgnored("src/out", true));
        Assert.True(rules.IsIgnored("src/gen", true));
        Assert.False(rules.IsIgnored("gen", true));
        Assert.True(rules.IsIgnored("a/b/c.tmp", false));
        Assert.True(rules.IsIgnored("docs/drafts", true));
        Assert.False(rules.IsIgnored("# generated", false));
    }
}

public class LiteralReplacerTests
{
    private static readonly LiteralReplacer Lumber = new([new LiteralReplacement("LumberjackHollow", "Name")]);

    [Fact]
    public void Text_EverySpellingBecomesItsTransform()
    {
        var (text, hits) = Lumber.InText("LumberjackHollow lumberjack-hollow lumberjack_hollow lumberjackHollow LUMBERJACKHOLLOW lumberjackhollow");

        Assert.Equal("{{Name}} {{Name:kebab}} {{Name:snake}} {{Name:camel}} {{Name:upper}} {{Name:lower}}", text);
        Assert.Equal(6, hits);
    }

    [Fact]
    public void Name_UsesTheDotForm() =>
        Assert.Equal(("{{Name.kebab}}.config.json", 1), Lumber.InName("lumberjack-hollow.config.json"));

    [Fact]
    public void OneWordLiteral_OnlyAsTyped()
    {
        var replacer = new LiteralReplacer([new LiteralReplacement("Game", "Name")]);

        Assert.Equal(("class {{Name}} : gameObject", 1), replacer.InText("class Game : gameObject"));
    }

    [Fact]
    public void LongestFirst_AndSeveralVariables()
    {
        var replacer = new LiteralReplacer([new LiteralReplacement("Acme", "Company"), new LiteralReplacement("AcmeTools", "Name")]);

        Assert.Equal(("{{Name}} by {{Company}}", 2), replacer.InText("AcmeTools by Acme"));
    }

    [Fact]
    public void EscapeExisting_OnlyNamesThatWouldRender()
    {
        var names = new HashSet<string> { "Name", "Year" };

        Assert.Equal("<h1>\\{{Name}}</h1> {{ item }} {{Other}} \\{{Year:upper}} \\{{Name}}",
            TemplateImporter.EscapeExisting("<h1>{{Name}}</h1> {{ item }} {{Other}} {{Year:upper}} \\{{Name}}", names));
    }
}

public class TemplateImporterTests : GenerationTestBase
{
    private string Source()
    {
        var src = Dir("LumberjackHollow");
        Write(Path.Combine(src, "LumberjackHollow.csproj"), "<Project><RootNamespace>LumberjackHollow</RootNamespace></Project>");
        Write(Path.Combine(src, "src", "Program.cs"), "namespace LumberjackHollow; // {{Name}} stays as written");
        Write(Path.Combine(src, "config", "lumberjack-hollow.json"), "{ \"id\": \"lumberjack-hollow\" }");
        Write(Path.Combine(src, "bin", "Debug", "app.dll"), "binary output");
        Write(Path.Combine(src, ".git", "HEAD"), "ref");
        File.WriteAllBytes(Path.Combine(src, "logo.png"), [.. "LumberjackHollow"u8, 0, 1]);
        File.WriteAllText(Path.Combine(src, "notes.txt"), "LumberjackHollow notes", new UTF8Encoding(true));
        return src;
    }

    private static readonly LiteralReplacer Lumber = new([new LiteralReplacement("LumberjackHollow", "Name")]);

    [Fact]
    public async Task Preview_IgnoredNotLookedInto_HitsCounted()
    {
        var preview = await TemplateImporter.PreviewAsync(Source(), new IgnoreRules(IgnoreRules.Defaults), Lumber);

        var byPath = preview.Entries.ToDictionary(e => e.Path);
        Assert.True(byPath[".git"].IsIgnored);
        Assert.True(byPath["bin"].IsIgnored);
        Assert.DoesNotContain("bin/Debug", byPath.Keys);
        Assert.Equal("{{Name}}.csproj", byPath["LumberjackHollow.csproj"].TargetPath);
        Assert.Equal(2, byPath["LumberjackHollow.csproj"].Hits); // the name and the RootNamespace
        Assert.Equal("config/{{Name.kebab}}.json", byPath["config/lumberjack-hollow.json"].TargetPath);
        Assert.True(byPath["logo.png"].IsBinary);
        Assert.Equal(0, byPath["logo.png"].Hits);
        Assert.Equal(5, preview.FileCount);
        Assert.Empty(preview.Problems);
    }

    [Fact]
    public async Task Preview_TwoFilesBecomingOne_IsAProblem()
    {
        var src = Dir("src");
        Write(Path.Combine(src, "Foo.txt"), "a");
        Write(Path.Combine(src, "Bar.txt"), "b");
        var replacer = new LiteralReplacer([new LiteralReplacement("Foo", "Name"), new LiteralReplacement("Bar", "Name")]);

        var preview = await TemplateImporter.PreviewAsync(src, new IgnoreRules([]), replacer);

        Assert.Contains("would all become \"{{Name}}.txt\"", preview.Problems.Single());
    }

    [Fact]
    public async Task Import_ThenGenerate_GivesTheProjectBackUnderANewName()
    {
        var library = new TemplateLibrary("Personal", Path.Combine(Root, "library"), LibrarySource.Personal);
        var manifest = NewTemplate.Create("Lumber", "lumber", TemplateKind.Project, "C#");

        var result = await TemplateImporter.ImportAsync(Source(), new IgnoreRules(IgnoreRules.Defaults), Lumber, library, manifest);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        var content = result.Entry!.ContentFolder;
        Assert.Equal("namespace {{Name}}; // \\{{Name}} stays as written", Read(Path.Combine(content, "src", "Program.cs")));
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(Path.Combine(content, "notes.txt"))[..3]);
        Assert.Equal([.. "LumberjackHollow"u8, 0, 1], File.ReadAllBytes(Path.Combine(content, "logo.png")));
        Assert.False(Directory.Exists(Path.Combine(content, "bin")));

        var staged = await new ProjectGenerator(new CommandRunner()).StageAsync(result.Entry, Context(("Name", "PineValley")), Dir("out"));
        await staged.Staged!.PlaceAsync();
        var project = Path.Combine(Root, "out", "PineValley");
        Assert.Equal("namespace PineValley; // {{Name}} stays as written", Read(Path.Combine(project, "src", "Program.cs")));
        Assert.Equal("{ \"id\": \"pine-valley\" }", Read(Path.Combine(project, "config", "pine-valley.json")));
        Assert.True(File.Exists(Path.Combine(project, "PineValley.csproj")));
    }

    [Fact]
    public async Task Import_IdTaken_LeavesTheExistingTemplateAlone()
    {
        var library = new TemplateLibrary("Personal", Path.Combine(Root, "library"), LibrarySource.Personal);
        var manifest = NewTemplate.Create("Lumber", "lumber", TemplateKind.Project, "C#");
        await TemplateStore.CreateAsync(library, manifest);

        var result = await TemplateImporter.ImportAsync(Source(), new IgnoreRules([]), Lumber, library, manifest);

        Assert.Contains("already has", result.Errors[0]);
        Assert.True(File.Exists(Path.Combine(library.Root, "lumber", "template.json")));
    }
}
