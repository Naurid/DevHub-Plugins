using System.Text.Json.Nodes;
using Xunit;

namespace DevHub.Templates.Core.Tests;

public class ManifestSerializerTests
{
    /// <summary>The example from docs/TEMPLATE_FORMAT.md §3 (with its comments).</summary>
    private const string SpecExample = """
        {
          "schemaVersion": 1,
          "id": "unity-game",
          "kind": "project",                    // "project" | "item"
          "name": "Unity Game",
          "description": "URP project with asmdef-per-domain layout",
          "language": "C#",
          "categories": ["Unity", "Game"],
          "variables": [
            {
              "name": "Name",
              "label": "Project name",
              "type": "string",
              "default": "MyGame",
              "required": true,
              "pattern": "^[A-Za-z][A-Za-z0-9_]*$",
              "choices": []
            },
            { "name": "InitGit", "type": "bool", "default": true }
          ],
          "rootFolder": "{{Name}}",
          "baseCommand": {
            "command": "dotnet",
            "args": ["new", "console", "-n", "{{Name}}", "-o", "."],
            "runIn": "root"
          },
          "steps": [
            { "op": "createFolder", "path": "src" },
            { "op": "move", "from": "*.cs", "to": "src/{{Name}}/" },
            { "op": "rename", "from": "src/{{Name}}/Program.cs", "to": "Main.cs" },
            { "op": "delete", "path": "README.md" },
          ],
          "postCreate": [
            { "name": "Initialize git", "command": "git", "args": ["init"],
              "workingDirectory": ".", "when": "InitGit", "continueOnError": false }
          ],
          "binaryExtensions": [".png", ".fbx"]
        }
        """;

    [Fact]
    public void Read_SpecExample_ReadsEveryPart()
    {
        var result = ManifestSerializer.Read(SpecExample);

        Assert.True(result.Succeeded, result.Error);
        var m = result.Manifest!;
        Assert.Equal(("unity-game", TemplateKind.Project, "Unity Game", "C#"), (m.Id, m.Kind, m.Name, m.Language));
        Assert.Equal(["Unity", "Game"], m.Categories);
        Assert.Equal("MyGame", m.Variables[0].DefaultText);
        Assert.True(m.Variables[0].Required);
        Assert.Equal((VariableType.Bool, "true"), (m.Variables[1].Type, m.Variables[1].DefaultText));
        Assert.Equal(("dotnet", RunIn.Root), (m.BaseCommand!.Command, m.BaseCommand.RunIn));
        Assert.Equal(["new", "console", "-n", "{{Name}}", "-o", "."], m.BaseCommand.Args);
        Assert.Equal([StepOp.CreateFolder, StepOp.Move, StepOp.Rename, StepOp.Delete], m.Steps.Select(s => s.Op));
        Assert.Equal(("*.cs", "src/{{Name}}/"), (m.Steps[1].From, m.Steps[1].To));
        Assert.Equal(("Initialize git", "InitGit"), (m.PostCreate[0].Name, m.PostCreate[0].When));
        Assert.Equal([".png", ".fbx"], m.BinaryExtensions);
        Assert.Empty(ManifestValidator.Validate(m));
    }

    [Fact]
    public void Read_MinimalManifest_FillsDefaults()
    {
        var m = ManifestSerializer.Read("""{ "schemaVersion": 1, "id": "x", "name": "X" }""").Manifest!;

        Assert.Equal((TemplateKind.Project, "Any", "{{Name}}"), (m.Kind, m.Language, m.EffectiveRootFolder));
        Assert.Empty(m.Categories);
        Assert.Null(m.BaseCommand);
    }

    [Fact]
    public void Write_ThenRead_KeepsEverything()
    {
        var original = ManifestSerializer.Read(SpecExample).Manifest!;

        var again = ManifestSerializer.Read(ManifestSerializer.Write(original)).Manifest!;

        Assert.Equal(ManifestSerializer.Write(original), ManifestSerializer.Write(again));
        Assert.Equal(original.Steps, again.Steps);
    }

    [Fact]
    public void Write_UsesCamelCaseNamesAndEnumsAndLeavesOutNulls()
    {
        var json = ManifestSerializer.Write(new TemplateManifest
        {
            Id = "x", Name = "X", Kind = TemplateKind.Item,
            Steps = [new TemplateStep { Op = StepOp.CreateFolder, Path = "src" }],
        });

        var node = JsonNode.Parse(json)!;
        Assert.Equal("item", (string)node["kind"]!);
        Assert.Equal("createFolder", (string)node["steps"]![0]!["op"]!);
        Assert.Null(node["description"]);
        Assert.Null(node["steps"]![0]!["from"]);
    }

    [Fact]
    public void ReadWrite_UnknownFields_SurviveAtEveryLevel()
    {
        const string json = """
            { "schemaVersion": 1, "id": "x", "name": "X", "futureTop": { "a": 1 },
              "variables": [ { "name": "Name", "futureVar": [1, 2] } ],
              "baseCommand": { "command": "dotnet", "futureCmd": "y" },
              "steps": [ { "op": "delete", "path": "a", "futureStep": true } ],
              "postCreate": [ { "name": "n", "command": "git", "futurePost": 3 } ] }
            """;

        var written = JsonNode.Parse(ManifestSerializer.Write(ManifestSerializer.Read(json).Manifest!))!;

        Assert.Equal(1, (int)written["futureTop"]!["a"]!);
        Assert.Equal(2, written["variables"]![0]!["futureVar"]!.AsArray().Count);
        Assert.Equal("y", (string)written["baseCommand"]!["futureCmd"]!);
        Assert.True((bool)written["steps"]![0]!["futureStep"]!);
        Assert.Equal(3, (int)written["postCreate"]![0]!["futurePost"]!);
    }

    [Theory]
    [InlineData("""{ "id": "x", "name": "X" }""", "no schemaVersion")]
    [InlineData("""{ "schemaVersion": 2, "id": "x" }""", "newer DevHub")]
    [InlineData("""{ "schemaVersion": 0, "id": "x" }""", "invalid schemaVersion")]
    [InlineData("""{ "schemaVersion": 1, "kind": "folder" }""", "isn't valid")]
    [InlineData("""{ "schemaVersion": 1, "id": """, "isn't valid")]
    [InlineData("null", "empty")]
    public void Read_BadManifest_ExplainsWhy(string json, string expected)
    {
        var result = ManifestSerializer.Read(json);

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Error);
    }
}
