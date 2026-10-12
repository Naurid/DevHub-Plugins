using DevHub.Templates.Core.Commands;
using DevHub.Templates.Core.Generation;
using Xunit;

namespace DevHub.Templates.Core.Tests;

public class UndoTests : GenerationTestBase
{
    private static string[] Paths(FileTree tree) => tree.Entries.Select(e => e.ToString()).ToArray();

    [Fact]
    public async Task Undo_TakesBackEditsInReverseOrder_AndThePlacedResultFollows()
    {
        var template = await Template(new TemplateManifest { Id = "t", Name = "T", Variables = [new TemplateVariable { Name = "Name" }] },
            ("Program.cs", "p"), ("notes.txt", "n"), ("docs/a.md", "a"));
        var parent = Dir("out");
        var project = (await new ProjectGenerator(new CommandRunner()).StageAsync(template, Context(("Name", "Game")), parent)).Staged!;
        var original = Paths(project.Tree);

        Assert.False(project.CanUndo);
        Assert.Null(project.ApplyEdit(new TemplateStep { Op = StepOp.Move, From = "Program.cs", To = "src/Program.cs" }));
        Assert.Null(project.ApplyEdit(new TemplateStep { Op = StepOp.Delete, Path = "docs" }));
        Assert.NotNull(project.ApplyEdit(new TemplateStep { Op = StepOp.Delete, Path = "missing" })); // refused: nothing to undo for it

        Assert.Equal(StepOp.Delete, project.UndoEdit()!.Op);
        Assert.Contains("docs/a.md", Paths(project.Tree));
        Assert.Single(project.Edits);
        Assert.Equal(StepOp.Move, project.UndoEdit()!.Op);
        Assert.Equal(original, Paths(project.Tree));
        Assert.False(project.CanUndo);
        Assert.Null(project.UndoEdit());

        Assert.Null(project.ApplyEdit(new TemplateStep { Op = StepOp.Delete, Path = "notes.txt" }));
        await project.PlaceAsync();
        Assert.Equal(["docs/a.md", "Program.cs"], Files(Path.Combine(parent, "Game")));
    }

    [Fact]
    public async Task Undo_ItemDeleteOfAnExistingFile_IsNoLongerDeleted()
    {
        var target = Dir("game");
        Write(Path.Combine(target, "keep.txt"), "x");
        var template = await Template(new TemplateManifest { Id = "i", Name = "I", Kind = TemplateKind.Item }, ("New.cs", "n"));
        var item = (await ItemApplier.StageAsync(template, Context(), target)).Staged!;

        item.ApplyEdit(new TemplateStep { Op = StepOp.Delete, Path = "keep.txt" });
        item.UndoEdit();
        await item.ApplyAsync(new HashSet<string>());

        Assert.Equal(["keep.txt", "New.cs"], Files(target));
    }
}
