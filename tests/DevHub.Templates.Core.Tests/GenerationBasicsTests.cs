using System.Text;
using DevHub.Templates.Core.Generation;
using DevHub.Templates.Core.Rendering;
using Xunit;

namespace DevHub.Templates.Core.Tests;

public class GlobTests
{
    [Theory]
    [InlineData("*.cs", "Program.cs", true)]
    [InlineData("*.cs", "src/Program.cs", false)]
    [InlineData("**/*.cs", "Program.cs", true)]
    [InlineData("**/*.cs", "src/a/b/Program.cs", true)]
    [InlineData("src/**", "src/a/b.txt", true)]
    [InlineData("src/**", "src", false)]
    [InlineData("src/**/obj", "src/obj", true)]
    [InlineData("src/**/obj", "src/a/b/obj", true)]
    [InlineData("src/*/obj", "src/a/b/obj", false)]
    [InlineData("?ead*.md", "README.md", true)]
    [InlineData("README.MD", "readme.md", true)]
    [InlineData("a.b", "aXb", false)]
    [InlineData("bin/", "bin", true)]
    public void IsMatch(string pattern, string path, bool expected) =>
        Assert.Equal(expected, new Glob(pattern).IsMatch(path));
}

public class SafePathTests
{
    private const string Root = @"C:\work\project";

    [Theory]
    [InlineData("src/App.cs", @"C:\work\project\src\App.cs")]
    [InlineData(".", @"C:\work\project")]
    [InlineData("src/../x", @"C:\work\project\x")]
    public void Resolve_InsideTheRoot(string relative, string expected) =>
        Assert.Equal(expected, SafePath.Resolve(Root, relative));

    [Theory]
    [InlineData("../x")]
    [InlineData("src/../../x")]
    [InlineData(@"C:\Windows")]
    [InlineData("/etc")]
    [InlineData("D:x")]
    [InlineData("../project-evil/x")]
    public void Resolve_OutsideTheRoot_IsNull(string relative) =>
        Assert.Null(SafePath.Resolve(Root, relative));
}

public class ContentFilesTests : GenerationTestBase
{
    private static readonly RenderContext NameContext = Context(("Name", "Timber"));

    [Fact]
    public void IsBinary_ByExtension_OrNulByte_ButNotUtf16Text()
    {
        var png = Path.Combine(Root, "a.png");
        var data = Path.Combine(Root, "a.custom");
        var utf16 = Path.Combine(Root, "a.txt");
        File.WriteAllBytes(png, [1, 2, 3]);
        File.WriteAllBytes(data, [65, 0, 66]);
        File.WriteAllText(utf16, "hello", Encoding.Unicode);

        Assert.True(ContentFiles.IsBinary(png, []));
        Assert.True(ContentFiles.IsBinary(data, []));
        Assert.False(ContentFiles.IsBinary(utf16, []));
        Assert.True(ContentFiles.IsBinary(utf16, [".TXT"]));
    }

    [Fact]
    public async Task Copy_Utf8WithBom_KeepsBomAndLineEndings()
    {
        var source = Path.Combine(Root, "in.cs");
        var target = Path.Combine(Root, "out", "out.cs");
        File.WriteAllText(source, "class {{Name}}\r\n{\n}\r\n", new UTF8Encoding(true));

        await ContentFiles.CopyAsync(source, target, NameContext, []);

        var bytes = File.ReadAllBytes(target);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("class Timber\r\n{\n}\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task Copy_Utf8WithoutBom_GetsNone()
    {
        var source = Path.Combine(Root, "in.txt");
        var target = Path.Combine(Root, "out.txt");
        File.WriteAllText(source, "é {{Name}}", new UTF8Encoding(false));

        await ContentFiles.CopyAsync(source, target, NameContext, []);

        Assert.Equal(Encoding.UTF8.GetBytes("é Timber"), File.ReadAllBytes(target));
    }

    [Fact]
    public async Task Copy_Utf16_IsRenderedInUtf16()
    {
        var source = Path.Combine(Root, "in.txt");
        var target = Path.Combine(Root, "out.txt");
        File.WriteAllText(source, "{{Name}}", Encoding.Unicode);

        await ContentFiles.CopyAsync(source, target, NameContext, []);

        var bytes = File.ReadAllBytes(target);
        Assert.Equal([0xFF, 0xFE], bytes[..2]);
        Assert.Equal("Timber", Encoding.Unicode.GetString(bytes[2..]));
    }

    [Fact]
    public async Task Copy_BinaryAndNonUtf8_AreCopiedUnchanged()
    {
        var binary = Path.Combine(Root, "logo.png");
        var ansi = Path.Combine(Root, "old.txt");
        byte[] binaryBytes = [.. "{{Name}}"u8, 0, 1];
        byte[] ansiBytes = [.. "{{Name}} "u8, 0xE9]; // "é" in Windows-1252: invalid UTF-8
        File.WriteAllBytes(binary, binaryBytes);
        File.WriteAllBytes(ansi, ansiBytes);

        await ContentFiles.CopyAsync(binary, Path.Combine(Root, "o1"), NameContext, []);
        await ContentFiles.CopyAsync(ansi, Path.Combine(Root, "o2"), NameContext, []);

        Assert.Equal(binaryBytes, File.ReadAllBytes(Path.Combine(Root, "o1")));
        Assert.Equal(ansiBytes, File.ReadAllBytes(Path.Combine(Root, "o2")));
    }
}

public class FileTreeTests
{
    private static FileTree Tree(params string[] files)
    {
        var tree = new FileTree();
        foreach (var file in files)
        {
            if (file.EndsWith('/'))
                tree.AddFolder(file, EntryOrigin.Command);
            else
                tree.AddFile(file, EntryOrigin.Command, "x:" + file);
        }
        return tree;
    }

    private static RenderedStep Step(StepOp op, string? path = null, string? from = null, string? to = null) =>
        new(op, path, from, to, null);

    private static string[] Paths(FileTree tree) => tree.Entries.Select(e => e.ToString()).ToArray();

    [Fact]
    public void AddFile_AddsItsFolders() =>
        Assert.Equal(["src/", "src/a/", "src/a/x.cs"], Paths(Tree("src/a/x.cs")));

    [Fact]
    public void CreateFolder_WithParents() =>
        Assert.Equal(["src/", "src/App/"], Paths(Apply(Tree(), Step(StepOp.CreateFolder, "src/App"))));

    [Fact]
    public void Move_GlobIntoFolder_KeepsNamesAndRecordsTheMove()
    {
        var tree = Apply(Tree("Program.cs", "Util.cs", "README.md"), Step(StepOp.Move, from: "*.cs", to: "src/App/"));

        Assert.Equal(["README.md", "src/", "src/App/", "src/App/Program.cs", "src/App/Util.cs"], Paths(tree));
        var moved = tree["src/App/Program.cs"]!;
        Assert.True(moved.IsMoved);
        Assert.Equal(("Program.cs", "x:Program.cs"), (moved.OriginalPath, moved.Source));
    }

    [Fact]
    public void Move_SingleToANewPath() =>
        Assert.Equal(["src/", "src/Main.cs"], Paths(Apply(Tree("Program.cs"), Step(StepOp.Move, from: "Program.cs", to: "src/Main.cs"))));

    [Fact]
    public void Move_AFolder_MovesEverythingInIt() =>
        Assert.Equal(["src/", "src/lib/", "src/lib/a/", "src/lib/a/x.cs"],
            Paths(Apply(Tree("lib/a/x.cs"), Step(StepOp.Move, from: "lib", to: "src/"))));

    [Fact]
    public void Move_EmptiesAFolder_TheFolderGoes_ButNotItsNonEmptyParent() =>
        Assert.Equal(["a/", "a/keep.txt", "src/", "src/x.cs"],
            Paths(Apply(Tree("a/b/c/x.cs", "a/keep.txt"), Step(StepOp.Move, from: "a/b/c/x.cs", to: "src/"))));

    [Fact]
    public void Move_EmptiedFolders_AreNotRecordedAsRemoved()
    {
        var tree = Apply(Tree("old/x.cs"), Step(StepOp.Move, from: "old/x.cs", to: "src/"));

        Assert.Null(tree["old"]);
        Assert.Empty(tree.Removed);
    }

    [Fact]
    public void Move_GlobMatchingNothing_IsFine() =>
        Assert.Equal(["a.txt"], Paths(Apply(Tree("a.txt"), Step(StepOp.Move, from: "*.cs", to: "src/"))));

    [Theory]
    [InlineData("missing.cs", "src/", "doesn't exist")]
    [InlineData("*.cs", "src/One.cs", "matches 2 items")]
    [InlineData("a.cs", "b.cs", "already exists")]
    [InlineData("lib", "lib/inner/", "into itself")]
    [InlineData("a.cs", "file.txt/", "is a file")]
    public void Move_Problems_ChangeNothing(string from, string to, string error)
    {
        var tree = Tree("a.cs", "b.cs", "file.txt", "lib/x.cs");
        var before = Paths(tree);

        var result = tree.Apply(Step(StepOp.Move, from: from, to: to));

        Assert.Contains(error, result);
        Assert.Equal(before, Paths(tree));
    }

    [Fact]
    public void Rename_InPlace_AndCaseOnly()
    {
        var tree = Apply(Tree("src/Program.cs", "readme.md"),
            Step(StepOp.Rename, from: "src/Program.cs", to: "Main.cs"),
            Step(StepOp.Rename, from: "readme.md", to: "README.md"));

        Assert.Equal(["README.md", "src/", "src/Main.cs"], Paths(tree));
    }

    [Fact]
    public void Rename_GlobOrMissing_IsRefused()
    {
        Assert.Contains("one path", Tree("a.cs").Apply(Step(StepOp.Rename, from: "*.cs", to: "b.cs")));
        Assert.Contains("doesn't exist", Tree("a.cs").Apply(Step(StepOp.Rename, from: "x.cs", to: "b.cs")));
    }

    [Fact]
    public void Delete_FolderTakesItsContents_AndRemovedIsRecorded()
    {
        var tree = Tree("obj/a.dll", "obj/b/c.dll", "src/x.cs");

        Assert.Null(tree.Apply(Step(StepOp.Delete, "obj")));

        Assert.Equal(["src/", "src/x.cs"], Paths(tree));
        Assert.Equal(4, tree.Removed.Count);
    }

    [Fact]
    public void Delete_GlobAnywhere() =>
        Assert.Equal(["src/", "src/x.cs"],
            Paths(Apply(Tree("a.tmp", "src/x.cs", "src/y.tmp"), Step(StepOp.Delete, "**/*.tmp"))));

    [Fact]
    public void AddFile_ReplacesAndReportsTheEarlierFile()
    {
        var tree = Tree("Program.cs");

        var replaced = tree.AddFile("Program.cs", EntryOrigin.Template, "staged");

        Assert.Equal(EntryOrigin.Command, replaced!.Origin);
        Assert.Equal(EntryOrigin.Template, tree["Program.cs"]!.Origin);
    }

    [Fact]
    public void Scan_SkipsNamedFoldersAndRecordsSources()
    {
        var root = Directory.CreateTempSubdirectory("devhub-tree-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git", "objects"));
            File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "x");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "a.cs"), "x");

            var tree = FileTree.Scan(root, EntryOrigin.Existing, [".git"]);

            Assert.Equal(["src/", "src/a.cs"], Paths(tree));
            Assert.Equal(Path.Combine(root, "src", "a.cs"), tree["src/a.cs"]!.Source);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static FileTree Apply(FileTree tree, params RenderedStep[] steps)
    {
        foreach (var step in steps)
            Assert.Null(tree.Apply(step));
        return tree;
    }
}
