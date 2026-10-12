using DevHub.Templates.Core.Commands;
using DevHub.Templates.Core.Generation;
using DevHub.Templates.Core.Rendering;
using Xunit;

namespace DevHub.Templates.Core.Tests;

public class ToolAliasesTests : GenerationTestBase
{
    private string Editor(string root, string version)
    {
        var exe = Path.Combine(Root, root, version, "Editor", "Unity.exe");
        Write(exe, "");
        return exe;
    }

    [Fact]
    public void UnityEditors_NewestFirst_AcrossFolders()
    {
        Editor("hub", "2022.3.10f1");
        var newest = Editor("hub", "6000.3.13f1");
        Editor("other", "6000.2.7f2");
        Dir(@"hub\6000.4.0b1-broken"); // no Unity.exe: not an editor

        var editors = ToolAliases.UnityEditors([Path.Combine(Root, "hub"), Path.Combine(Root, "other")]);

        Assert.Equal(["6000.3.13f1", "6000.2.7f2", "2022.3.10f1"], editors.Select(e => e.Version));
        Assert.Equal(newest, editors[0].Path);
    }

    [Theory]
    [InlineData("6000.3.13f1", "6000.3.9f1", 1)]
    [InlineData("6000.3.0f1", "6000.3.0b5", 1)]
    [InlineData("2022.3.10f1", "6000.0.1f1", -1)]
    [InlineData("6000.3.13f1", "6000.3.13f1", 0)]
    public void UnityVersions_CompareAsNumbers(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(ToolAliases.UnityVersionComparer.Instance.Compare(a, b)));

    [Fact]
    public void Resolve_UnknownAlias_IsNull() => Assert.Null(ToolAliases.Resolve("godot"));
}

/// <summary>
/// The built-in library against the real tools: each test runs when its tool is installed and passes
/// quietly otherwise. npm templates need the network: set DEVHUB_NETWORK_TESTS=1 to run them. Unity
/// takes minutes and a license, so only its command is checked.
/// </summary>
public class BuiltInLibraryTests : GenerationTestBase
{
    private static readonly TemplateLibrary BuiltIn = new("Built-in", TemplateLibraries.BuiltInRoot, LibrarySource.BuiltIn);
    private static readonly CommandRunner Runner = new(ToolAliases.Resolve);

    private static bool Has(string tool) => Runner.Resolve(tool) is not null;
    private static bool Network => Environment.GetEnvironmentVariable("DEVHUB_NETWORK_TESTS") == "1";

    private static async Task<TemplateEntry> Load(string id) => (await TemplateStore.ListAsync(BuiltIn)).Single(t => t.Id == id);

    /// <summary>Generates the project (base command, files, steps), places it, runs the post-create commands; returns its folder.</summary>
    private async Task<string> Generate(string id, params (string Name, string Value)[] entered)
    {
        var template = await Load(id);
        var check = VariableValues.Check(template.Manifest!, entered.ToDictionary(e => e.Name, e => e.Value));
        Assert.True(check.Succeeded, string.Join("; ", check.Issues.Select(i => i.Message)));
        var context = new RenderContext(check.Values, "Tester", DateTimeOffset.Now);

        var staged = await new ProjectGenerator(Runner).StageAsync(template, context, Dir("out"));
        Assert.True(staged.Succeeded, string.Join("; ", staged.Errors) + "\n" + string.Join("\n", staged.CommandResult?.Output.Select(o => o.Text) ?? []));
        var placed = await staged.Staged!.PlaceAsync();
        Assert.True(placed.Succeeded, placed.Error);

        var results = await PostCreate.RunAsync(Runner, TemplateRendering.PostCreate(template.Manifest!, context), placed.Path!);
        Assert.All(results, r => Assert.True(r.Succeeded, r.Error + "\n" + string.Join("\n", r.Output.Select(o => o.Text))));
        return placed.Path!;
    }

    [Fact]
    public async Task Every_BuiltIn_IsValid_AndHasLanguageAndCategory()
    {
        var all = await TemplateStore.ListAsync(BuiltIn);

        Assert.True(all.Count >= 17);
        Assert.All(all, t =>
        {
            Assert.True(t.IsUsable, t.Id + ": " + string.Join("; ", t.Problems));
            Assert.NotEmpty(t.Manifest!.Categories);
            Assert.False(string.IsNullOrWhiteSpace(t.Manifest.Description), t.Id);
        });
    }

    [Fact]
    public async Task Unity_RunsTheNewestEditorInTheParentFolder()
    {
        var manifest = (await Load("unity-project")).Manifest!;
        var command = TemplateRendering.BaseCommand(manifest, new RenderContext(new Dictionary<string, string> { ["Name"] = "Woods" }, "", DateTimeOffset.Now))!;

        Assert.Equal("@unity -batchmode -quit -createProject Woods -logFile -", command.DisplayText);
        Assert.Equal(RunIn.Parent, manifest.BaseCommand!.RunIn);
    }

    [Fact]
    public async Task DotnetClassLibrary_InSrc_WithSolution()
    {
        if (!Has("dotnet"))
            return;
        var project = await Generate("dotnet-classlib", ("Name", "Tools"), ("Git", "false"));

        Assert.Contains("src/Tools/Tools.csproj", Files(project));
        Assert.Contains("src/Tools/Class1.cs", Files(project));
        Assert.Contains(Files(project), f => f is "Tools.sln" or "Tools.slnx");
    }

    [Fact]
    public async Task DotnetXunit()
    {
        if (!Has("dotnet"))
            return;
        var project = await Generate("dotnet-xunit", ("Name", "Tools.Tests"));

        Assert.Contains("Tools.Tests.csproj", Files(project));
    }

    [Fact]
    public async Task DotnetSolutionWithTests_TestsReferenceTheProject()
    {
        if (!Has("dotnet"))
            return;
        var project = await Generate("dotnet-solution", ("Name", "Shop"), ("Kind", "classlib"), ("Git", "false"));

        Assert.Contains("src/Shop/Shop.csproj", Files(project));
        Assert.Contains("../../src/Shop/Shop.csproj".Replace('/', '\\'), Read(Path.Combine(project, "tests", "Shop.Tests", "Shop.Tests.csproj")));
        Assert.Contains(Files(project), f => f is "Shop.sln" or "Shop.slnx");
    }

    [Fact]
    public async Task DotnetAddProject_ItemTemplateOfCommandsOnly()
    {
        if (!Has("dotnet"))
            return;
        var folder = Dir("app");
        var sln = await Runner.RunAsync(new RenderedCommand("sln", "dotnet", ["new", "sln", "--name", "App"], "."), folder);
        Assert.True(sln.Succeeded, sln.Error);
        var template = await Load("dotnet-add-project");
        var check = VariableValues.Check(template.Manifest!, new Dictionary<string, string> { ["Name"] = "Core" });
        var context = new RenderContext(check.Values, "", DateTimeOffset.Now);

        var staged = await ItemApplier.StageAsync(template, context, folder);
        Assert.True(staged.Staged!.Changes(new HashSet<string>()).IsEmpty);
        await staged.Staged.ApplyAsync(new HashSet<string>());
        var results = await PostCreate.RunAsync(Runner, TemplateRendering.PostCreate(template.Manifest!, context), folder);

        Assert.All(results, r => Assert.True(r.Succeeded, r.Error));
        Assert.Contains("src/Core/Core.csproj", Files(folder));
        Assert.Contains("Core", Read(Path.Combine(folder, Files(folder).First(f => f.StartsWith("App.sln")))));
    }

    [Theory]
    [InlineData("bin", "src/main.rs")]
    [InlineData("lib", "src/lib.rs")]
    public async Task RustCrate(string kind, string file)
    {
        if (!Has("cargo"))
            return;
        var project = await Generate("rust-cargo", ("Name", "Forest Map"), ("Kind", kind), ("Git", "false"));

        Assert.EndsWith("forest-map", project);
        Assert.Contains(file, Files(project));
        Assert.Contains("name = \"forest-map\"", Read(Path.Combine(project, "Cargo.toml")));
        Assert.Contains("README.md", Files(project));
    }

    [Fact]
    public async Task GoModule()
    {
        if (!Has("go"))
            return;
        var project = await Generate("go-module", ("Name", "hello"), ("Module", "example.com/hello"), ("Git", "false"));

        Assert.StartsWith("module example.com/hello", Read(Path.Combine(project, "go.mod")));
        Assert.Contains("main.go", Files(project));
    }

    [Fact]
    public async Task PythonPackage_SrcLayout()
    {
        var project = await Generate("python-package", ("Name", "Tree Count"), ("Venv", "false"), ("Git", "false"));

        Assert.EndsWith("tree-count", project);
        Assert.Contains("src/tree_count/__init__.py", Files(project));
        Assert.Contains("tests/test_tree_count.py", Files(project));
        Assert.Contains("name = \"tree-count\"", Read(Path.Combine(project, "pyproject.toml")));
        Assert.Contains("import tree_count", Read(Path.Combine(project, "tests", "test_tree_count.py")));
    }

    [Theory]
    [InlineData("vite-app", "Framework", "react-ts", "vite.config.ts")]
    [InlineData("tauri-app", "Frontend", "vanilla-ts", "src-tauri/Cargo.toml")]
    public async Task Npm_WithTheNetwork(string id, string choiceName, string choice, string file)
    {
        if (!Network || !Has("npm"))
            return;
        var project = await Generate(id, ("Name", "Pine Valley"), (choiceName, choice), ("Install", "false"), ("Git", "false"));

        Assert.EndsWith("pine-valley", project);
        Assert.Contains(file, Files(project));
    }
}
