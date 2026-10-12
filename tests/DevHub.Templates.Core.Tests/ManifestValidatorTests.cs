using System.Text.Json;
using Xunit;

namespace DevHub.Templates.Core.Tests;

public class ManifestValidatorTests
{
    private static readonly TemplateVariable NameVariable = new() { Name = "Name" };

    private static TemplateManifest Valid(TemplateKind kind = TemplateKind.Project) => new()
    {
        Id = "my-template",
        Name = "My Template",
        Kind = kind,
        Variables = kind == TemplateKind.Project ? [NameVariable] : [],
    };

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    /// <summary>The fields of every issue (what the editor highlights).</summary>
    private static string[] Fields(TemplateManifest manifest) => ManifestValidator.Validate(manifest).Select(i => i.Field).ToArray();

    [Fact]
    public void Validate_MinimalProjectAndItem_HaveNoIssues()
    {
        Assert.Empty(Fields(Valid()));
        Assert.Empty(Fields(Valid(TemplateKind.Item)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unity-Game")]
    [InlineData("unity game")]
    [InlineData("unity--game")]
    [InlineData("-unity")]
    public void Validate_BadId_IsReported(string id) =>
        Assert.Equal(["id"], Fields(Valid() with { Id = id }));

    [Fact]
    public void Validate_EmptyNameOrLanguage_IsReported() =>
        Assert.Equal(["name", "language"], Fields(Valid() with { Name = " ", Language = "" }));

    [Fact]
    public void Validate_EmptyAndDuplicateCategories_AreReported() =>
        Assert.Equal(["categories[1]", "categories"], Fields(Valid() with { Categories = ["Unity", "", "unity"] }));

    [Theory]
    [InlineData("1Name")]
    [InlineData("Project Name")]
    [InlineData("project_name")]
    public void Validate_BadVariableName_IsReported(string name) =>
        Assert.Contains("variables[1].name", Fields(Valid() with { Variables = [NameVariable, new TemplateVariable { Name = name }] }));

    [Theory]
    [InlineData("Year")]
    [InlineData("guid3")]
    [InlineData("Author")]
    public void Validate_BuiltInVariableName_IsReported(string name) =>
        Assert.Equal(["variables[1].name"], Fields(Valid() with { Variables = [NameVariable, new TemplateVariable { Name = name }] }));

    [Fact]
    public void Validate_DuplicateVariable_IsReported() =>
        Assert.Equal(["variables"], Fields(Valid() with { Variables = [NameVariable, new TemplateVariable { Name = "name" }] }));

    [Fact]
    public void Validate_ChoiceRules()
    {
        var noOptions = new TemplateVariable { Name = "Kind", Type = VariableType.Choice };
        var badDefault = noOptions with { Choices = ["a", "b"], Default = Json("\"c\"") };
        var good = badDefault with { Default = Json("\"b\"") };

        Assert.Equal(["variables[1].choices"], Fields(Valid() with { Variables = [NameVariable, noOptions] }));
        Assert.Equal(["variables[1].default"], Fields(Valid() with { Variables = [NameVariable, badDefault] }));
        Assert.Empty(Fields(Valid() with { Variables = [NameVariable, good] }));
    }

    [Fact]
    public void Validate_BoolDefault_MustBeTrueOrFalse()
    {
        var variable = new TemplateVariable { Name = "Git", Type = VariableType.Bool, Default = Json("\"yes\"") };

        Assert.Equal(["variables[1].default"], Fields(Valid() with { Variables = [NameVariable, variable] }));
        Assert.Empty(Fields(Valid() with { Variables = [NameVariable, variable with { Default = Json("false") }] }));
    }

    [Fact]
    public void Validate_PatternRules()
    {
        var onBool = new TemplateVariable { Name = "Git", Type = VariableType.Bool, Pattern = "x" };
        var invalid = new TemplateVariable { Name = "Id", Pattern = "([" };
        var defaultMismatch = new TemplateVariable { Name = "Id", Pattern = "^[a-z]+$", Default = Json("\"ABC\"") };

        Assert.Equal(["variables[1].pattern"], Fields(Valid() with { Variables = [NameVariable, onBool] }));
        Assert.Equal(["variables[1].pattern"], Fields(Valid() with { Variables = [NameVariable, invalid] }));
        Assert.Equal(["variables[1].default"], Fields(Valid() with { Variables = [NameVariable, defaultMismatch] }));
    }

    [Fact]
    public void Validate_ItemTemplate_HasNoRootFolderOrBaseCommand() =>
        Assert.Equal(["rootFolder", "baseCommand"], Fields(Valid(TemplateKind.Item) with
        {
            RootFolder = "x",
            BaseCommand = new BaseCommand { Command = "dotnet" },
        }));

    [Fact]
    public void Validate_ProjectWithoutRootFolder_NeedsANameVariable()
    {
        Assert.Equal(["rootFolder"], Fields(Valid() with { Variables = [] }));
        Assert.Empty(Fields(Valid() with { Variables = [], RootFolder = "Fixed" }));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(" ")]
    public void Validate_RootFolder_IsOneName(string root) =>
        Assert.Equal(["rootFolder"], Fields(Valid() with { RootFolder = root }));

    [Fact]
    public void Validate_BaseCommand_NeedsACommand() =>
        Assert.Equal(["baseCommand.command"], Fields(Valid() with { BaseCommand = new BaseCommand { Command = "" } }));

    [Fact]
    public void Validate_Steps_NeedTheirFields()
    {
        var manifest = Valid() with
        {
            Steps =
            [
                new TemplateStep { Op = StepOp.CreateFolder },
                new TemplateStep { Op = StepOp.Move, From = "a" },
                new TemplateStep { Op = StepOp.Rename, From = "a", To = "b/c" },
                new TemplateStep { Op = StepOp.Delete, Path = "x" },
            ],
        };

        Assert.Equal(["steps[0].path", "steps[1].to", "steps[2].to"], Fields(manifest));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("src/../../x")]
    [InlineData("/abs")]
    [InlineData("C:/x")]
    [InlineData("src\\App")]
    public void Validate_StepPath_StaysRelativeAndInside(string path) =>
        Assert.Equal(["steps[0].path"], Fields(Valid() with { Steps = [new TemplateStep { Op = StepOp.Delete, Path = path }] }));

    [Fact]
    public void Validate_PostCreateRules()
    {
        var gitVar = new TemplateVariable { Name = "InitGit", Type = VariableType.Bool };
        var manifest = Valid() with
        {
            Variables = [NameVariable, gitVar],
            PostCreate =
            [
                new PostCreateCommand { Name = "", Command = "" },
                new PostCreateCommand { Name = "a", Command = "git", When = "Name" },
                new PostCreateCommand { Name = "b", Command = "git", WorkingDirectory = "../x" },
                new PostCreateCommand { Name = "c", Command = "git", When = "InitGit", WorkingDirectory = "src" },
            ],
        };

        Assert.Equal(["postCreate[0].name", "postCreate[0].command", "postCreate[1].when", "postCreate[2].workingDirectory"],
            Fields(manifest));
    }

    [Fact]
    public void Validate_BinaryExtension_NeedsItsDot() =>
        Assert.Equal(["binaryExtensions[0]"], Fields(Valid() with { BinaryExtensions = ["png", ".fbx"] }));
}
