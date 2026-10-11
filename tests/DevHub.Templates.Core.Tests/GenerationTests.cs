using DevHub.Templates.Core.Commands;
using DevHub.Templates.Core.Generation;
using DevHub.Templates.Core.Rendering;
using Xunit;

namespace DevHub.Templates.Core.Tests;

public class CommandRunnerTests : GenerationTestBase
{
    private static RenderedCommand Cmd(string command, params string[] args) => new("Test", command, args, ".");

    [Fact]
    public void Resolve_FindsCmdShimsOnPathWithPathExt()
    {
        var shim = Tool("npm", "echo hi");
        var runner = new CommandRunner(path: () => Path.GetDirectoryName(shim));

        Assert.Equal(shim, runner.Resolve("npm"), ignoreCase: true);
        Assert.Null(runner.Resolve("no-such-tool"));
    }

    [Fact]
    public async Task Run_CmdShim_GetsSeparateArgumentsAndStreamsOutput()
    {
        var shim = Tool("greet", "echo first=%~1", "echo second=%~2");
        var lines = new List<CommandOutputLine>();

        var result = await new CommandRunner().RunAsync(Cmd(shim, "My App", "x"), Root, new SyncProgress<CommandOutputLine>(lines.Add));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["first=My App", "second=x"], result.Output.Select(l => l.Text));
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public async Task Run_RealExecutable_InTheWorkingFolder()
    {
        var result = await new CommandRunner().RunAsync(Cmd("dotnet", "--version"), Root);

        Assert.True(result.Succeeded, result.Error);
        Assert.Matches(@"^\d+\.\d+", result.Output[0].Text);
    }

    [Fact]
    public async Task Run_NonZeroExit_IsAnError_WithStdErr()
    {
        var shim = Tool("fail", "echo bad thing 1>&2", "exit /b 3");

        var result = await new CommandRunner().RunAsync(Cmd(shim), Root);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("exit code 3", result.Error);
        Assert.Contains(result.Output, l => l is { IsError: true, Text: "bad thing " });
    }

    [Fact]
    public async Task Run_NotFound_SaysSo()
    {
        var result = await new CommandRunner(path: () => "").RunAsync(Cmd("cargo", "new"), Root);

        Assert.Null(result.ExitCode);
        Assert.Contains("wasn't found", result.Error);
    }

    [Fact]
    public async Task Run_Alias_IsResolvedByTheApp()
    {
        var shim = Tool("fake-unity", "echo unity %*");
        var runner = new CommandRunner(alias => alias == "unity" ? shim : null);

        var ok = await runner.RunAsync(Cmd("@unity", "-batchmode"), Root);
        var unknown = await runner.RunAsync(Cmd("@godot"), Root);

        Assert.Equal("unity -batchmode", ok.Output.Single().Text);
        Assert.Contains("doesn't know where godot", unknown.Error);
    }

    [Fact]
    public async Task Run_Cancel_KillsTheProcessQuickly()
    {
        var shim = Tool("slow", "ping -n 30 127.0.0.1 > nul");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var started = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CommandRunner().RunAsync(Cmd(shim), Root, ct: cts.Token));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }
}

/// <summary>Progress that reports on the calling thread (Progress&lt;T&gt; posts later, which races the assertions).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

public class ProjectGeneratorTests : GenerationTestBase
{
    private static TemplateManifest Manifest(BaseCommand? command = null, params TemplateStep[] steps) => new()
    {
        Id = "app", Name = "App", Kind = TemplateKind.Project,
        Variables = [new TemplateVariable { Name = "Name" }],
        BaseCommand = command,
        Steps = steps,
    };

    private string FakeDotnetNew() => Tool("fake-dotnet-new",
        "echo class Program {} > Program.cs",
        "echo ^<Project /^> > %~1.csproj",
        "echo from the tool > README.md");

    [Fact]
    public async Task Stage_BaseCommandThenContentThenSteps_ThenPlace()
    {
        var template = await Template(
            Manifest(new BaseCommand { Command = FakeDotnetNew(), Args = ["{{Name}}"] },
                new TemplateStep { Op = StepOp.Move, From = "*.cs", To = "src/{{Name}}/" },
                new TemplateStep { Op = StepOp.Move, From = "{{Name}}.csproj", To = "src/{{Name}}/" }),
            ("README.md", "# {{Name}}\n"),
            ("docs/{{Name.kebab}}.md", "about {{Name}}"));
        var parent = Dir("projects");

        var staged = await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "MyGame")), parent);

        Assert.True(staged.Succeeded, string.Join("; ", staged.Errors));
        var project = staged.Staged!;
        Assert.Equal(EntryOrigin.Template, project.Tree["README.md"]!.Origin); // the template's file wins
        Assert.Equal(EntryOrigin.Command, project.Tree["src/MyGame/Program.cs"]!.Origin);
        Assert.True(project.Tree["src/MyGame/Program.cs"]!.IsMoved);
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(parent), e => !Path.GetFileName(e).StartsWith(".devhub-staging-"));

        var placed = await project.PlaceAsync();

        Assert.True(placed.Succeeded, placed.Error);
        var target = Path.Combine(parent, "MyGame");
        Assert.Equal(["docs/my-game.md", "README.md", "src/MyGame/MyGame.csproj", "src/MyGame/Program.cs"], Files(target));
        Assert.Equal("# MyGame\n", Read(Path.Combine(target, "README.md")));
        Assert.Empty(StagingFolders(parent));
    }

    [Fact]
    public async Task Stage_RunInParent_TheCommandMakesTheRoot()
    {
        var tool = Tool("fake-cargo-new", "mkdir %~1", "echo fn main() {} > %~1\\main.rs");
        var template = await Template(Manifest(new BaseCommand { Command = tool, Args = ["{{Name}}"], RunIn = RunIn.Parent }));
        var parent = Dir("projects");

        var staged = await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "crab")), parent);
        var placed = await staged.Staged!.PlaceAsync();

        Assert.True(placed.Succeeded);
        Assert.Equal(["main.rs"], Files(Path.Combine(parent, "crab")));
    }

    [Fact]
    public async Task Stage_RunInParent_CommandNotMakingTheRoot_IsAnError()
    {
        var tool = Tool("lazy", "echo did nothing");
        var template = await Template(Manifest(new BaseCommand { Command = tool, RunIn = RunIn.Parent }));
        var parent = Dir("projects");

        var staged = await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "x")), parent);

        Assert.False(staged.Succeeded);
        Assert.Contains("didn't create the folder", staged.Errors[0]);
        Assert.Empty(StagingFolders(parent));
    }

    [Fact]
    public async Task Stage_FailingBaseCommand_LeavesNothingBehind()
    {
        var template = await Template(Manifest(new BaseCommand { Command = Tool("broken", "exit /b 1") }));
        var parent = Dir("projects");

        var staged = await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "x")), parent);

        Assert.False(staged.Succeeded);
        Assert.Equal(1, staged.CommandResult!.ExitCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
    }

    [Fact]
    public async Task Stage_FailingStep_LeavesNothingBehind()
    {
        var template = await Template(Manifest(null, new TemplateStep { Op = StepOp.Delete, Path = "missing.txt" }), ("a.txt", "a"));
        var parent = Dir("projects");

        var staged = await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "x")), parent);

        Assert.Contains("Step 1", staged.Errors[0]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
    }

    [Fact]
    public async Task Stage_BadRootName_IsRefusedBeforeAnythingRuns()
    {
        var template = await Template(Manifest(new BaseCommand { Command = Tool("never", "exit /b 9") }));

        var staged = await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "..")), Dir("p"));

        Assert.Contains("Project folder", staged.Errors[0]);
    }

    [Fact]
    public async Task PreviewEdits_ChangeTheResult_AndAreKept()
    {
        var template = await Template(Manifest(), ("Program.cs", "x"), ("notes.txt", "y"));
        var parent = Dir("projects");
        var project = (await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "Game")), parent)).Staged!;

        Assert.Null(project.ApplyEdit(new TemplateStep { Op = StepOp.CreateFolder, Path = "src/Game" }));
        Assert.Null(project.ApplyEdit(new TemplateStep { Op = StepOp.Move, From = "Program.cs", To = "src/Game/" }));
        Assert.Null(project.ApplyEdit(new TemplateStep { Op = StepOp.Delete, Path = "notes.txt" }));
        Assert.NotNull(project.ApplyEdit(new TemplateStep { Op = StepOp.Delete, Path = "../outside" }));
        Assert.NotNull(project.ApplyEdit(new TemplateStep { Op = StepOp.Delete, Path = "nothing" }));
        await project.PlaceAsync();

        Assert.Equal(3, project.Edits.Count);
        Assert.Equal(["src/Game/Program.cs"], Files(Path.Combine(parent, "Game")));
    }

    [Fact]
    public async Task Place_IntoANonEmptyFolder_IsRefused_ButAnEmptyOneIsFilled()
    {
        var template = await Template(Manifest(), ("a.txt", "a"));
        var parent = Dir("projects");
        Directory.CreateDirectory(Path.Combine(parent, "Empty"));
        var generator = new ProjectGenerator(new CommandRunner());

        // Something appears in the folder while the preview is open.
        var stagedTaken = (await generator.StageAsync(template, Context(("Name", "Taken")), parent)).Staged!;
        Write(Path.Combine(parent, "Taken", "mine.txt"), "keep");
        var taken = await stagedTaken.PlaceAsync();
        var empty = await (await generator.StageAsync(template, Context(("Name", "Empty")), parent)).Staged!.PlaceAsync();

        Assert.Contains("isn't empty", taken.Error);
        Assert.Equal(["mine.txt"], Files(Path.Combine(parent, "Taken")));
        Assert.True(empty.Succeeded);
        Assert.Equal(["a.txt"], Files(Path.Combine(parent, "Empty")));
    }

    [Fact]
    public async Task Stage_ReportsTheCommandsOutputInOrder()
    {
        var tool = Tool("chatty", "for /L %%i in (1,1,60) do @echo line %%i");
        var template = await Template(Manifest(new BaseCommand { Command = tool }));
        var lines = new List<string>();

        await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "x")), Dir("p"),
            progress: new SyncProgress<GenerationProgress>(p => { if (p.Output is { } o) lock (lines) lines.Add(o.Text); }));

        Assert.Equal(Enumerable.Range(1, 60).Select(i => $"line {i}"), lines);
    }

    [Fact]
    public async Task Stage_IntoANonEmptyFolder_IsRefusedBeforeAnythingRuns()
    {
        var template = await Template(Manifest(new BaseCommand { Command = Tool("never", "exit /b 9") }));
        var parent = Dir("projects");
        Write(Path.Combine(parent, "Taken", "mine.txt"), "keep");

        var staged = await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "Taken")), parent);

        Assert.Contains("isn't empty", staged.Errors[0]);
        Assert.Null(staged.CommandResult);
        Assert.Empty(StagingFolders(parent));
    }

    [Theory]
    [InlineData("relative\\folder", "Choose the folder")]
    [InlineData("", "Choose the folder")]
    public void TargetFor_NeedsAFullParentPath(string parent, string error) =>
        Assert.Contains(error, ProjectGenerator.TargetFor(Manifest(), Context(("Name", "x")), parent).Error);

    [Fact]
    public void TargetFor_GivesTheFolderName()
    {
        var target = ProjectGenerator.TargetFor(Manifest() with { RootFolder = "{{Name:kebab}}" }, Context(("Name", "My Game")), Root);

        Assert.Equal("my-game", target.Path);
        Assert.Contains("doesn't exist", ProjectGenerator.TargetFor(Manifest(), Context(("Name", "x")), Path.Combine(Root, "missing")).Error);
    }

    [Fact]
    public async Task Raw_OnlyTheBaseCommand_NoContentNoSteps()
    {
        var template = await Template(
            Manifest(new BaseCommand { Command = FakeDotnetNew(), Args = ["{{Name}}"] },
                new TemplateStep { Op = StepOp.Move, From = "*.cs", To = "src/" }) with
            {
                PostCreate = [new PostCreateCommand { Name = "Init", Command = "git", Args = ["init"] }],
            },
            ("extra.txt", "from the template"));
        var parent = Dir("projects");

        var staged = await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "Raw")), parent, raw: true);
        await staged.Staged!.PlaceAsync();

        Assert.Equal(["Program.cs", "Raw.csproj", "README.md"], Files(Path.Combine(parent, "Raw")));
        Assert.Equal(["Base command"], TemplateRendering.AllCommands(template.Manifest!, Context(("Name", "Raw")), raw: true).Select(c => c.Name));
        Assert.Equal(2, TemplateRendering.AllCommands(template.Manifest!, Context(("Name", "Raw"))).Count);
    }

    [Fact]
    public async Task Raw_WithoutABaseCommand_IsRefused()
    {
        var template = await Template(Manifest(), ("a.txt", "a"));

        var staged = await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "x")), Dir("p"), raw: true);

        Assert.Contains("no base command", staged.Errors[0]);
    }

    [Fact]
    public async Task Dispose_WithoutPlacing_DeletesStaging()
    {
        var template = await Template(Manifest(), ("a.txt", "a"));
        var parent = Dir("projects");

        var staged = await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "x")), parent);
        staged.Staged!.Dispose();

        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
    }

    [Fact]
    public async Task RealDotnetNewConsole_RawTemplate()
    {
        var template = await Template(Manifest(new BaseCommand
        {
            Command = "dotnet",
            Args = ["new", "console", "-n", "{{Name}}", "-o", ".", "--no-restore"],
        }));
        var parent = Dir("projects");

        var staged = await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "Hello")), parent);
        Assert.True(staged.Succeeded, string.Join("; ", staged.Errors) + string.Join("\n", staged.CommandResult?.Output.Select(o => o.Text) ?? []));
        var placed = await staged.Staged!.PlaceAsync();

        Assert.True(placed.Succeeded);
        Assert.Contains("Hello.csproj", Files(Path.Combine(parent, "Hello")));
        Assert.Contains("Program.cs", Files(Path.Combine(parent, "Hello")));
    }
}

public class ItemApplierTests : GenerationTestBase
{
    private static TemplateManifest Item(params TemplateStep[] steps) => new()
    {
        Id = "class", Name = "Class", Kind = TemplateKind.Item,
        Variables = [new TemplateVariable { Name = "Name" }],
        Steps = steps,
    };

    [Fact]
    public async Task Apply_AddsFiles_AndMovesExistingOnes()
    {
        var target = Dir("game");
        Write(Path.Combine(target, "Player.cs"), "player");
        Write(Path.Combine(target, "Enemy.cs"), "enemy");
        Write(Path.Combine(target, "old", "Util.cs"), "util");
        Write(Path.Combine(target, ".git", "HEAD"), "git");
        var template = await Template(Item(
                new TemplateStep { Op = StepOp.Move, From = "*.cs", To = "src/" },
                new TemplateStep { Op = StepOp.Move, From = "old/Util.cs", To = "src/Util.cs" }),
            ("{{Name}}.cs", "class {{Name}} {}"));

        var staged = await ItemApplier.StageAsync(template, Context(("Name", "Boss")), target);
        Assert.True(staged.Succeeded, string.Join("; ", staged.Errors));
        Assert.Equal(["Player.cs", "Enemy.cs", "old/Util.cs"],
            new[] { "src/Player.cs", "src/Enemy.cs", "src/Util.cs" }.Select(p => staged.Staged!.Tree[p]!.OriginalPath));
        Assert.Null(staged.Staged!.Tree["old"]); // emptied by the move
        Assert.True(File.Exists(Path.Combine(target, "Player.cs"))); // the disk is untouched until applied

        var applied = await staged.Staged!.ApplyAsync(new HashSet<string>());

        Assert.True(applied.Succeeded);
        Assert.Equal([".git/HEAD", "src/Boss.cs", "src/Enemy.cs", "src/Player.cs", "src/Util.cs"], Files(target));
        Assert.Equal("class Boss {}", Read(Path.Combine(target, "src", "Boss.cs")));
        Assert.Equal("player", Read(Path.Combine(target, "src", "Player.cs")));
        Assert.False(Directory.Exists(Path.Combine(target, "old"))); // emptied by the move
        Assert.Empty(StagingFolders(target));
    }

    [Fact]
    public async Task Conflicts_OverwriteOnlyWhenAllowed()
    {
        var target = Dir("game");
        Write(Path.Combine(target, "A.cs"), "mine A");
        Write(Path.Combine(target, "B.cs"), "mine B");
        var template = await Template(Item(), ("A.cs", "template A"), ("B.cs", "template B"), ("C.cs", "template C"));

        var staged = (await ItemApplier.StageAsync(template, Context(("Name", "x")), target)).Staged!;
        Assert.Equal(["A.cs", "B.cs"], staged.Conflicts.Order());
        await staged.ApplyAsync(new HashSet<string>(["B.cs"]));

        Assert.Equal("mine A", Read(Path.Combine(target, "A.cs")));
        Assert.Equal("template B", Read(Path.Combine(target, "B.cs")));
        Assert.Equal("template C", Read(Path.Combine(target, "C.cs")));
    }

    [Fact]
    public async Task Apply_DeletesWhatAStepDeleted_Only()
    {
        var target = Dir("web");
        Write(Path.Combine(target, "dist", "bundle.js"), "x");
        Write(Path.Combine(target, "index.html"), "keep");
        var template = await Template(Item(new TemplateStep { Op = StepOp.Delete, Path = "dist" }));

        await (await ItemApplier.StageAsync(template, Context(("Name", "x")), target)).Staged!.ApplyAsync(new HashSet<string>());

        Assert.Equal(["index.html"], Files(target));
    }

    [Fact]
    public async Task Stage_ProjectTemplate_IsRefused()
    {
        var template = await Template(new TemplateManifest { Id = "p", Name = "P", Variables = [new TemplateVariable { Name = "Name" }] });

        Assert.Contains("isn't an item template", (await ItemApplier.StageAsync(template, Context(), Dir("t"))).Errors[0]);
    }
}

public class PostCreateTests : GenerationTestBase
{
    private RenderedCommand Command(string name, string tool, bool continueOnError = false, string folder = ".") =>
        new(name, tool, [], folder) { ContinueOnError = continueOnError };

    [Fact]
    public async Task Run_StopsAtAFailure_UnlessContinueOnError()
    {
        var ok = Tool("ok", "echo ok");
        var bad = Tool("bad", "exit /b 2");
        var runner = new CommandRunner();

        var stopped = await PostCreate.RunAsync(runner, [Command("a", bad), Command("b", ok)], Root);
        var continued = await PostCreate.RunAsync(runner, [Command("a", bad, continueOnError: true), Command("b", ok)], Root);

        Assert.Single(stopped);
        Assert.Equal([false, true], continued.Select(r => r.Succeeded));
    }

    [Fact]
    public async Task Run_InItsWorkingFolder_ButNeverOutsideTheProject()
    {
        Dir("src");
        var where = Tool("where-am-i", "cd");

        var results = await PostCreate.RunAsync(new CommandRunner(),
            [Command("in src", where, true, "src"), Command("outside", where, true, "../..")], Root);

        Assert.EndsWith("src", results[0].Output.Single().Text);
        Assert.Contains("outside the project", results[1].Error);
    }
}

public class PreviewEditsTests
{
    [Fact]
    public void ToTemplateSteps_PutsValuesBackAsPlaceholders()
    {
        var manifest = new TemplateManifest
        {
            Id = "t", Name = "T",
            Variables =
            [
                new TemplateVariable { Name = "Name" },
                new TemplateVariable { Name = "TestsName" },
                new TemplateVariable { Name = "Git", Type = VariableType.Bool },
            ],
        };
        var values = new Dictionary<string, string> { ["Name"] = "MyGame", ["TestsName"] = "MyGameTests", ["Git"] = "true" };
        TemplateStep[] edits =
        [
            new() { Op = StepOp.Move, From = "MyGameTests/*.cs", To = "tests/MyGameTests/" },
            new() { Op = StepOp.Rename, From = "src/MyGame/Program.cs", To = "MyGameApp.cs" },
            new() { Op = StepOp.Delete, Path = "true.txt" },
        ];

        var steps = PreviewEdits.ToTemplateSteps(edits, manifest, values);

        Assert.Equal("{{TestsName}}/*.cs", steps[0].From);
        Assert.Equal("tests/{{TestsName}}/", steps[0].To);
        Assert.Equal(("src/{{Name}}/Program.cs", "{{Name}}App.cs"), (steps[1].From, steps[1].To));
        Assert.Equal("true.txt", steps[2].Path);
    }
}
