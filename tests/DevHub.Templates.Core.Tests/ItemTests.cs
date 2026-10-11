using DevHub.Templates.Core.Commands;
using DevHub.Templates.Core.Generation;
using DevHub.Templates.Core.Items;
using Xunit;

namespace DevHub.Templates.Core.Tests;

public class NamespaceInferenceTests : GenerationTestBase
{
    [Fact]
    public void Csproj_NameAndTheFoldersInBetween()
    {
        Write(Path.Combine(Root, "Game", "Game.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var folder = Dir(@"Game\src\Player AI");

        var guess = NamespaceInference.Infer(folder);

        Assert.Equal("Game.src.Player_AI", guess!.Namespace);
        Assert.Equal("from Game.csproj", guess.Source);
    }

    [Fact]
    public void Csproj_RootNamespace_WithMSBuildProjectName()
    {
        Write(Path.Combine(Root, "App", "My.App.csproj"),
            "<Project><PropertyGroup><RootNamespace>Company.$(MSBuildProjectName)</RootNamespace></PropertyGroup></Project>");

        Assert.Equal("Company.My.App.Models", NamespaceInference.Infer(Dir(@"App\Models"))!.Namespace);
        Assert.Equal("Company.My.App", NamespaceInference.Infer(Dir("App"))!.Namespace);
    }

    [Fact]
    public void FolderNotCreatedYet_StillWorks()
    {
        Write(Path.Combine(Root, "Lib", "Lib.csproj"), "<Project />");

        Assert.Equal("Lib.New.Deeper", NamespaceInference.Infer(Path.Combine(Root, "Lib", "New", "Deeper"))!.Namespace);
    }

    [Fact]
    public void Asmdef_RootNamespace()
    {
        Write(Path.Combine(Root, "Game", "Assets", "Scripts", "Core", "Core.asmdef"), """{ "name": "Core", "rootNamespace": "Studio.Core" }""");

        Assert.Equal("Studio.Core.Combat", NamespaceInference.Infer(Dir(@"Game\Assets\Scripts\Core\Combat"))!.Namespace);
    }

    [Fact]
    public void UnityProject_NameAndFoldersUnderAssets_WithoutScripts()
    {
        Dir(@"Lumber Jack\ProjectSettings");
        Write(Path.Combine(Root, "Lumber Jack", "Assets", "Scripts", "Plain", "Plain.asmdef"), """{ "name": "Plain" }""");

        Assert.Equal("Lumber_Jack.Player", NamespaceInference.Infer(Dir(@"Lumber Jack\Assets\Scripts\Player"))!.Namespace);
        Assert.Equal("Lumber_Jack.Plain", NamespaceInference.Infer(Dir(@"Lumber Jack\Assets\Scripts\Plain"))!.Namespace);
        Assert.Equal("Lumber_Jack.Art", NamespaceInference.Infer(Dir(@"Lumber Jack\Assets\Art"))!.Namespace);
    }

    [Fact]
    public void RepositoryRootWithoutProject_StopsThere()
    {
        Write(Path.Combine(Root, "Outer.csproj"), "<Project />"); // above the repository: doesn't count
        Dir(@"repo\.git");

        Assert.Null(NamespaceInference.Infer(Dir(@"repo\docs")));
    }

    [Theory]
    [InlineData("3D", "_3D")]
    [InlineData("my-folder", "my_folder")]
    [InlineData("Ünïcode", "Ünïcode")]
    public void Identifier(string name, string expected) => Assert.Equal(expected, NamespaceInference.Identifier(name));
}

public class ItemChangesTests : GenerationTestBase
{
    [Fact]
    public async Task Changes_AddedOverwrittenKeptMovedDeleted()
    {
        var target = Dir("game");
        Write(Path.Combine(target, "A.cs"), "a");
        Write(Path.Combine(target, "B.cs"), "b");
        Write(Path.Combine(target, "old.txt"), "old");
        Write(Path.Combine(target, "Move.cs"), "m");
        var template = await Template(new TemplateManifest
        {
            Id = "t", Name = "T", Kind = TemplateKind.Item,
            Steps =
            [
                new TemplateStep { Op = StepOp.Move, From = "Move.cs", To = "src/" },
                new TemplateStep { Op = StepOp.Delete, Path = "old.txt" },
            ],
        }, ("A.cs", "new a"), ("B.cs", "new b"), ("New.cs", "new"));

        var staged = (await ItemApplier.StageAsync(template, Context(), target)).Staged!;
        var changes = staged.Changes(new HashSet<string>(["A.cs"]));

        Assert.Equal(["New.cs"], changes.Added);
        Assert.Equal(["A.cs"], changes.Overwritten);
        Assert.Equal(["B.cs"], changes.Kept);
        Assert.Equal([("Move.cs", "src/Move.cs")], changes.Moved);
        Assert.Equal(["old.txt"], changes.Deleted);
        staged.Dispose();
    }

    [Fact]
    public async Task Changes_PreviewDeleteOfAnExistingFile_IsListed()
    {
        var target = Dir("game");
        Write(Path.Combine(target, "keep.txt"), "x");
        var template = await Template(new TemplateManifest { Id = "t", Name = "T", Kind = TemplateKind.Item }, ("New.cs", "new"));
        var staged = (await ItemApplier.StageAsync(template, Context(), target)).Staged!;

        Assert.Null(staged.ApplyEdit(new TemplateStep { Op = StepOp.Delete, Path = "keep.txt" }));

        Assert.Equal(["keep.txt"], staged.Changes(new HashSet<string>()).Deleted);
        staged.Dispose();
    }
}

public class BuiltInItemTests : GenerationTestBase
{
    private static readonly TemplateLibrary BuiltIn = new("Built-in", TemplateLibraries.BuiltInRoot, LibrarySource.BuiltIn);

    [Theory]
    [InlineData("csharp-class", "Player.cs", "namespace Game.Actors;\n\npublic class Player\n{\n}\n")]
    [InlineData("csharp-interface", "Player.cs", "namespace Game.Actors;\n\npublic interface Player\n{\n}\n")]
    [InlineData("unity-monobehaviour", "Player.cs", "using UnityEngine;\n\nnamespace Game.Actors\n{\n    public class Player : MonoBehaviour\n")]
    [InlineData("unity-scriptableobject", "Player.cs", "    [CreateAssetMenu(fileName = \"Player\", menuName = \"Data/Player\")]")]
    [InlineData("python-module", "player.py", "\"\"\"Player: \"\"\"")]
    public async Task Apply_WritesTheFile(string id, string file, string expected)
    {
        var template = (await TemplateStore.ListAsync(BuiltIn)).Single(t => t.Id == id);
        Assert.True(template.IsUsable, string.Join("; ", template.Problems));
        var target = Dir("game");
        var values = VariableValuesFor(template.Manifest!, ("Name", "Player"), ("Namespace", "Game.Actors"));

        var staged = await ItemApplier.StageAsync(template, Context(values), target);
        Assert.True(staged.Succeeded, string.Join("; ", staged.Errors));
        await staged.Staged!.ApplyAsync(new HashSet<string>());

        Assert.Contains(expected, Read(Path.Combine(target, file)).ReplaceLineEndings("\n"));
        Assert.Empty(StagingFolders(target));
    }

    private static (string, string)[] VariableValuesFor(TemplateManifest manifest, params (string Name, string Value)[] entered)
    {
        var check = Rendering.VariableValues.Check(manifest, entered.ToDictionary(e => e.Name, e => e.Value));
        Assert.True(check.Succeeded, string.Join("; ", check.Issues.Select(i => i.Message)));
        return check.Values.Select(v => (v.Key, v.Value)).ToArray();
    }
}
