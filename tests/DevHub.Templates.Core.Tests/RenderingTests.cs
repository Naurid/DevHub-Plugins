using System.Text.Json;
using DevHub.Templates.Core.Rendering;
using Xunit;

namespace DevHub.Templates.Core.Tests;

public class CaseTransformsTests
{
    [Theory] // the table in docs/TEMPLATE_FORMAT.md §5
    [InlineData("pascal", "MyCoolGame")]
    [InlineData("camel", "myCoolGame")]
    [InlineData("kebab", "my-cool-game")]
    [InlineData("snake", "my_cool_game")]
    [InlineData("upper", "MY COOL_GAME")]
    [InlineData("lower", "my cool_game")]
    public void Apply_SpecTable(string transform, string expected) =>
        Assert.Equal(expected, CaseTransforms.Apply(transform, "My cool_game"));

    [Theory]
    [InlineData("myHTTPServer", "my|HTTP|Server")]
    [InlineData("HTTPServer", "HTTP|Server")]
    [InlineData("LumberjackHollow", "Lumberjack|Hollow")]
    [InlineData("version2Beta", "version2|Beta")]
    [InlineData("Unity3D game", "Unity3|D|game")]
    [InlineData("io-utils_v2.final", "io|utils|v2|final")]
    [InlineData("  spaced   out ", "spaced|out")]
    [InlineData("ABC", "ABC")]
    [InlineData("", "")]
    public void Words_SplitOnSeparatorsAndCaseChanges(string value, string expected) =>
        Assert.Equal(expected, string.Join('|', CaseTransforms.Words(value)));

    [Fact]
    public void Apply_Acronyms_AreNormalised()
    {
        Assert.Equal("MyHttpServer", CaseTransforms.Apply("pascal", "myHTTPServer"));
        Assert.Equal("myHttpServer", CaseTransforms.Apply("camel", "my HTTP server"));
        Assert.Equal("my-http-server", CaseTransforms.Apply("kebab", "MyHTTPServer"));
    }

    [Fact]
    public void Apply_UnknownTransform_IsNull() => Assert.Null(CaseTransforms.Apply("title", "x"));

    [Fact]
    public void Apply_Camel_OfNothing_IsEmpty() => Assert.Equal("", CaseTransforms.Apply("camel", " - "));
}

public class PlaceholderRendererTests
{
    private static int _guidCounter;

    private static RenderContext Context(params (string Name, string Value)[] variables) =>
        new(variables.ToDictionary(v => v.Name, v => v.Value), "Ada Lovelace",
            new DateTimeOffset(2026, 10, 11, 9, 30, 0, TimeSpan.Zero),
            () => new Guid(Interlocked.Increment(ref _guidCounter), 0, 0, new byte[8]));

    private static string Render(string text, params (string, string)[] variables) =>
        PlaceholderRenderer.Render(text, Context(variables));

    [Fact]
    public void Render_DeclaredVariable_AndTransform()
    {
        Assert.Equal("class MyGame : Game // my-game", Render("class {{Name:pascal}} : Game // {{Name:kebab}}", ("Name", "my game")));
        Assert.Equal("my game", Render("{{Name}}", ("Name", "my game")));
    }

    [Theory] // other template languages in template files must come out untouched
    [InlineData("<p>{{ message }}</p>")]                                   // Vue / Angular
    [InlineData("{% if user %}Hello {{ user.name }}{% endif %}")]          // Jinja / Liquid
    [InlineData("token: ${{ secrets.TOKEN }}\nref: ${{github.ref}}")]      // GitHub Actions
    [InlineData("{{#each items}}<li>{{this}}</li>{{/each}}")]              // Handlebars
    [InlineData("{{{body}}}")]                                              // triple-stash
    [InlineData("{{.Values.image}} {{- .Release.Name -}}")]                 // Go / Helm
    [InlineData("{{Unknown}} {{Name:title}} {{ Name }} {{Name:}} {{:pascal}}")]
    [InlineData("open {{ but never closed")]
    [InlineData("}} {{")]
    public void Render_NotOurs_IsLeftExactlyAsWritten(string text) =>
        Assert.Equal(text, Render(text, ("Name", "X")));

    [Fact]
    public void Render_MixedWithForeignSyntax_OnlyReplacesOurs() =>
        Assert.Equal("name: Timber\non: ${{ github.event }}\n{{#if x}}Timber{{/if}}",
            Render("name: {{Name}}\non: ${{ github.event }}\n{{#if x}}{{Name}}{{/if}}", ("Name", "Timber")));

    [Fact]
    public void Render_ValueContainingAPlaceholder_IsNotExpandedAgain() =>
        Assert.Equal("{{Other}} and b", Render("{{A}} and {{Other}}", ("A", "{{Other}}"), ("Other", "b")));

    [Fact]
    public void Render_Escape_GivesLiteralBraces()
    {
        Assert.Equal("{{Name}} is Timber", Render("\\{{Name}} is {{Name}}", ("Name", "Timber")));
        Assert.Equal("a {{ b", Render("a \\{{ b"));
    }

    [Fact]
    public void Render_NamesAreCaseSensitive() =>
        Assert.Equal("{{name}} Timber", Render("{{name}} {{Name}}", ("Name", "Timber")));

    [Fact]
    public void Render_BuiltIns()
    {
        var text = Render("© {{Year}} {{Author}}, {{Date}}");

        Assert.Equal("© 2026 Ada Lovelace, 2026-10-11", text);
    }

    [Fact]
    public void Render_Guids_StayTheSameWithinOneGeneration_AndDifferBetweenThem()
    {
        var context = Context();
        var sln = PlaceholderRenderer.Render("Project = {{Guid1}} / {{Guid2}}", context);
        var csproj = PlaceholderRenderer.Render("<ProjectGuid>{{Guid1}}</ProjectGuid>", context);
        var other = PlaceholderRenderer.Render("{{Guid1}}", Context());

        var guid1 = csproj["<ProjectGuid>".Length..^"</ProjectGuid>".Length];
        Assert.Contains(guid1, sln);
        Assert.NotEqual(guid1, other);
        Assert.Matches("^[0-9A-F-]{36}$", guid1);
        Assert.NotEqual(guid1, sln.Split(" / ")[1]);
    }

    [Fact]
    public void RenderWithInfo_ReportsTheVariablesUsed() =>
        Assert.Equal(["Name", "Year"],
            PlaceholderRenderer.RenderWithInfo("{{Name}}-{{Year}}-{{Name:kebab}}-{{Nope}}", Context(("Name", "A"))).UsedVariables);

    [Fact]
    public void Render_TextWithoutBraces_IsReturnedAsIs() =>
        Assert.Equal("plain text", Render("plain text"));
}

public class PathRendererTests
{
    private static RenderContext Context(string name) =>
        new(new Dictionary<string, string> { ["Name"] = name }, "", DateTimeOffset.Now);

    [Fact]
    public void RenderPath_RendersEverySegment() =>
        Assert.Equal("src/Timber/Timber.csproj", PathRenderer.RenderPath("src/{{Name}}/{{Name}}.csproj", Context("Timber")).Path);

    [Fact]
    public void RenderPath_DotTransform_ForFileNamesWindowsAllows() =>
        Assert.Equal("docs/my-game.md/MyGame", PathRenderer.RenderPath("docs/{{Name.kebab}}.md/{{Name:pascal}}", Context("my game")).Path);

    [Fact]
    public void Render_DotTransform_OnlyInPaths() =>
        Assert.Equal("{{Name.kebab}}", PlaceholderRenderer.Render("{{Name.kebab}}", Context("my game")));

    [Fact]
    public void RenderPath_DotWithUnknownTransform_StaysAsWritten() =>
        Assert.Equal("{{Name.ext}}", PathRenderer.RenderPath("{{Name.ext}}", Context("x")).Path);

    [Fact]
    public void RenderPath_KeepsAFolderTargetsTrailingSlash() =>
        Assert.Equal("src/Timber/", PathRenderer.RenderPath("src/{{Name}}/", Context("Timber")).Path);

    [Theory]
    [InlineData("", "is empty")]
    [InlineData("a/b", "slash")]
    [InlineData("..\\evil", "slash")]
    [InlineData("..", "isn't allowed")]
    [InlineData("my:game", "Windows doesn't allow")]
    [InlineData("what?", "Windows doesn't allow")]
    [InlineData("con", "reserves")]
    [InlineData("Com1", "reserves")]
    [InlineData("game.", "ends with a dot")]
    [InlineData("game ", "ends with a dot or space")]
    public void RenderSegment_BadValue_IsRefusedNamingTheVariable(string value, string reason)
    {
        var result = PathRenderer.RenderSegment("{{Name}}", Context(value));

        Assert.False(result.Succeeded);
        Assert.Contains(reason, result.Error);
        Assert.Contains("{{Name}}", result.Error);
    }

    [Fact]
    public void RenderPath_ValueCantEscapeTheRoot()
    {
        var result = PathRenderer.RenderPath("src/{{Name}}/file.txt", Context("../../Windows"));

        Assert.False(result.Succeeded);
        Assert.Contains("slash", result.Error);
    }

    [Fact]
    public void RenderPath_DoubleSlash_IsAnEmptySegment() =>
        Assert.Contains("is empty", PathRenderer.RenderPath("src//x", Context("a")).Error);

    [Fact]
    public void RenderPath_GlobsOnlyWhenAllowed()
    {
        Assert.Equal("src/**/*.cs", PathRenderer.RenderPath("src/**/*.cs", Context("a"), allowGlobs: true).Path);
        Assert.False(PathRenderer.RenderPath("src/*.cs", Context("a")).Succeeded);
    }

    [Fact]
    public void RenderSegment_UnicodeNames_AreFine() =>
        Assert.Equal("Jätkä-ゲーム", PathRenderer.RenderSegment("{{Name}}", Context("Jätkä-ゲーム")).Path);
}

public class VariableValuesTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static readonly TemplateManifest Manifest = new()
    {
        Id = "t", Name = "T",
        Variables =
        [
            new TemplateVariable { Name = "Name", Required = true, Pattern = "^[A-Za-z][A-Za-z0-9]*$" },
            new TemplateVariable { Name = "Description", Default = Json("\"A game\"") },
            new TemplateVariable { Name = "InitGit", Type = VariableType.Bool, Default = Json("true") },
            new TemplateVariable { Name = "Pipeline", Type = VariableType.Choice, Choices = ["URP", "HDRP"], Default = Json("\"URP\"") },
            new TemplateVariable { Name = "Optional", Type = VariableType.Bool },
        ],
    };

    [Fact]
    public void Check_FillsDefaults()
    {
        var result = VariableValues.Check(Manifest, new Dictionary<string, string> { ["Name"] = "Timber" });

        Assert.True(result.Succeeded);
        Assert.Equal("Timber", result.Values["Name"]);
        Assert.Equal("A game", result.Values["Description"]);
        Assert.Equal(("true", "URP", "false"), (result.Values["InitGit"], result.Values["Pipeline"], result.Values["Optional"]));
    }

    [Fact]
    public void Check_NormalisesBools() =>
        Assert.Equal("false", VariableValues.Check(Manifest, new Dictionary<string, string> { ["Name"] = "A", ["InitGit"] = "False" }).Values["InitGit"]);

    [Fact]
    public void Check_ReportsEveryProblemPerVariable()
    {
        var result = VariableValues.Check(Manifest, new Dictionary<string, string>
        {
            ["Name"] = "1 bad",
            ["InitGit"] = "maybe",
            ["Pipeline"] = "Built-in",
        });

        Assert.Equal(["Name", "InitGit", "Pipeline"], result.Issues.Select(i => i.Variable));
    }

    [Fact]
    public void Check_RequiredStringMissing_IsReported() =>
        Assert.Contains("required", Assert.Single(VariableValues.Check(Manifest, new Dictionary<string, string>()).Issues).Message);
}

public class TemplateRenderingTests
{
    private static readonly TemplateManifest Manifest = ManifestSerializer.Read("""
        {
          "schemaVersion": 1, "id": "app", "name": "App",
          "variables": [ { "name": "Name" }, { "name": "InitGit", "type": "bool" }, { "name": "Restore", "type": "bool" } ],
          "baseCommand": { "command": "dotnet", "args": ["new", "console", "-n", "{{Name}}", "-o", "."] },
          "steps": [
            { "op": "move", "from": "*.cs", "to": "src/{{Name}}/" },
            { "op": "rename", "from": "src/{{Name}}/Program.cs", "to": "{{Name}}App.cs" },
            { "op": "delete", "path": "{{Name}}/obj" }
          ],
          "postCreate": [
            { "name": "Initialize git", "command": "git", "args": ["init"], "when": "InitGit" },
            { "name": "Restore {{Name}}", "command": "dotnet", "args": ["restore"], "when": "Restore", "workingDirectory": "src/{{Name}}" }
          ]
        }
        """).Manifest!;

    private static RenderContext Context(string name, bool git = true, bool restore = false) =>
        new(new Dictionary<string, string> { ["Name"] = name, ["InitGit"] = git ? "true" : "false", ["Restore"] = restore ? "true" : "false" },
            "", DateTimeOffset.Now);

    [Fact]
    public void RootFolder_DefaultsToName() =>
        Assert.Equal("My App", TemplateRendering.RootFolder(Manifest, Context("My App")).Path);

    [Fact]
    public void RootFolder_BadName_IsRefused() =>
        Assert.False(TemplateRendering.RootFolder(Manifest, Context("a/b")).Succeeded);

    [Fact]
    public void Commands_AreRendered_AndWhenConditionsApplied()
    {
        var commands = TemplateRendering.AllCommands(Manifest, Context("My App", git: true, restore: true));

        Assert.Equal(["Base command", "Initialize git", "Restore My App"], commands.Select(c => c.Name));
        Assert.Equal(["new", "console", "-n", "My App", "-o", "."], commands[0].Args);
        Assert.Equal("dotnet new console -n \"My App\" -o .", commands[0].DisplayText);
        Assert.Equal("src/My App", commands[2].WorkingDirectory);
        Assert.Equal(["Base command"], TemplateRendering.AllCommands(Manifest, Context("A", git: false)).Select(c => c.Name));
    }

    [Fact]
    public void Steps_AreRendered()
    {
        var steps = TemplateRendering.Steps(Manifest, Context("Timber"));

        Assert.All(steps, s => Assert.True(s.Succeeded, s.Error));
        Assert.Equal(("*.cs", "src/Timber/"), (steps[0].From, steps[0].To));
        Assert.Equal(("src/Timber/Program.cs", "TimberApp.cs"), (steps[1].From, steps[1].To));
        Assert.Equal("Timber/obj", steps[2].Path);
    }

    [Fact]
    public void Steps_BadValue_ReportsWhichPath()
    {
        var steps = TemplateRendering.Steps(Manifest, Context("..\\x"));

        Assert.All(steps, s => Assert.False(s.Succeeded));
        Assert.Contains("{{Name}}", steps[0].Error);
    }
}
