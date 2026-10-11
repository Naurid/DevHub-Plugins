using Xunit;

namespace DevHub.Templates.Core.Tests;

public sealed class TemplateStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("devhub-templates-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private TemplateLibrary Library(string name = "Personal", LibrarySource source = LibrarySource.Personal) =>
        new(name, Path.Combine(_root, name), source);

    private static TemplateManifest Manifest(string id = "console-app") => new()
    {
        Id = id,
        Name = "Console App",
        Language = "C#",
        Categories = ["Tools"],
        Variables = [new TemplateVariable { Name = "Name" }],
        BaseCommand = new BaseCommand { Command = "dotnet", Args = ["new", "console"] },
    };

    /// <summary>Puts a template folder on disk directly (as a team repo or the built-ins would have it).</summary>
    private static void WriteTemplate(TemplateLibrary library, string folder, string json, string? contentFile = null)
    {
        var path = Path.Combine(library.Root, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, TemplateStore.ManifestFileName), json);
        if (contentFile is not null)
        {
            Directory.CreateDirectory(Path.Combine(path, "content"));
            File.WriteAllText(Path.Combine(path, "content", contentFile), "x");
        }
    }

    [Fact]
    public async Task List_MissingLibraryFolder_IsEmpty() =>
        Assert.Empty(await TemplateStore.ListAsync(Library()));

    [Fact]
    public async Task Create_ThenList_FindsTheTemplate()
    {
        var library = Library();

        var created = await TemplateStore.CreateAsync(library, Manifest());

        Assert.True(created.Succeeded, string.Join("; ", created.Errors));
        var entry = Assert.Single(await TemplateStore.ListAsync(library));
        Assert.True(entry.IsUsable);
        Assert.Equal(("console-app", "Personal/console-app"), (entry.Id, entry.Reference));
        Assert.Equal("Console App", entry.Manifest!.Name);
        Assert.True(Directory.Exists(entry.ContentFolder));
        Assert.False(entry.HasContent); // a raw template: just its base command
    }

    [Fact]
    public async Task Create_InvalidManifest_WritesNothing()
    {
        var library = Library();

        var result = await TemplateStore.CreateAsync(library, Manifest("Bad Id"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.StartsWith("id:"));
        Assert.False(Directory.Exists(library.Root));
    }

    [Fact]
    public async Task Create_TakenId_IsRefused()
    {
        var library = Library();
        await TemplateStore.CreateAsync(library, Manifest());

        var again = await TemplateStore.CreateAsync(library, Manifest());

        Assert.Contains("already has", Assert.Single(again.Errors));
    }

    [Fact]
    public async Task List_BrokenTemplates_AreListedWithTheirProblems()
    {
        var library = Library();
        WriteTemplate(library, "unreadable", "{ not json");
        WriteTemplate(library, "too-new", """{ "schemaVersion": 9, "id": "too-new" }""");
        WriteTemplate(library, "invalid", """{ "schemaVersion": 1, "id": "invalid", "name": "" }""");
        WriteTemplate(library, "folder-name", """{ "schemaVersion": 1, "id": "other-id", "name": "X", "rootFolder": "X" }""");
        Directory.CreateDirectory(Path.Combine(library.Root, "not-a-template")); // no template.json: ignored

        var entries = await TemplateStore.ListAsync(library);

        Assert.Equal(["folder-name", "invalid", "too-new", "unreadable"], entries.Select(e => e.Id));
        Assert.All(entries, e => Assert.False(e.IsUsable));
        Assert.Contains("doesn't match the folder name", Assert.Single(entries[0].Problems));
        Assert.Contains(entries[1].Problems, p => p.StartsWith("name:"));
        Assert.Null(entries[2].Manifest);
        Assert.Contains("newer DevHub", entries[2].Problems[0]);
        Assert.Contains("isn't valid", entries[3].Problems[0]);
    }

    [Fact]
    public async Task Save_ChangesTheManifest_AndAnIdChangeRenamesTheFolder()
    {
        var library = Library();
        var entry = (await TemplateStore.CreateAsync(library, Manifest())).Entry!;
        File.WriteAllText(Path.Combine(entry.ContentFolder, "keep.txt"), "x");

        var saved = await TemplateStore.SaveAsync(entry, entry.Manifest! with { Id = "cli-app", Categories = ["CLI"] });

        Assert.True(saved.Succeeded);
        var listed = Assert.Single(await TemplateStore.ListAsync(library));
        Assert.Equal("cli-app", listed.Id);
        Assert.Equal(["CLI"], listed.Manifest!.Categories);
        Assert.True(File.Exists(Path.Combine(listed.ContentFolder, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(listed.Folder, "template.json.tmp")));
    }

    [Fact]
    public async Task Save_IdTakenByAnother_IsRefused()
    {
        var library = Library();
        var first = (await TemplateStore.CreateAsync(library, Manifest("a"))).Entry!;
        await TemplateStore.CreateAsync(library, Manifest("b"));

        var result = await TemplateStore.SaveAsync(first, first.Manifest! with { Id = "b" });

        Assert.False(result.Succeeded);
        Assert.Equal(["a", "b"], (await TemplateStore.ListAsync(library)).Select(e => e.Id));
    }

    [Fact]
    public async Task Delete_RemovesTheFolder()
    {
        var library = Library();
        var entry = (await TemplateStore.CreateAsync(library, Manifest())).Entry!;

        Assert.True(TemplateStore.Delete(entry).Succeeded);
        Assert.Empty(await TemplateStore.ListAsync(library));
    }

    [Fact]
    public async Task BuiltIns_AreReadOnly()
    {
        var builtIn = Library("Built-in", LibrarySource.BuiltIn);
        WriteTemplate(builtIn, "console-app", ManifestSerializer.Write(Manifest()));
        var entry = Assert.Single(await TemplateStore.ListAsync(builtIn));

        Assert.False((await TemplateStore.CreateAsync(builtIn, Manifest("x"))).Succeeded);
        Assert.False((await TemplateStore.SaveAsync(entry, entry.Manifest!)).Succeeded);
        Assert.Contains("read-only", Assert.Single(TemplateStore.Delete(entry).Errors));
        Assert.True(Directory.Exists(entry.Folder));
    }

    [Fact]
    public async Task Duplicate_CopiesIntoAWritableLibrary_KeepingTheIdWhenFree()
    {
        var builtIn = Library("Built-in", LibrarySource.BuiltIn);
        var personal = Library();
        WriteTemplate(builtIn, "console-app", ManifestSerializer.Write(Manifest()), contentFile: "Program.cs");
        var original = Assert.Single(await TemplateStore.ListAsync(builtIn));

        var copy = await TemplateStore.DuplicateAsync(original, personal);
        var second = await TemplateStore.DuplicateAsync(original, personal);

        Assert.True(copy.Succeeded);
        Assert.Equal(("console-app", "Console App"), (copy.Entry!.Id, copy.Entry.Manifest!.Name));
        Assert.True(File.Exists(Path.Combine(copy.Entry.ContentFolder, "Program.cs")));
        Assert.Equal("console-app-copy", second.Entry!.Id);
        Assert.True(second.Entry.IsUsable);
    }

    [Fact]
    public async Task Duplicate_InTheSameLibrary_GetsANewIdAndName()
    {
        var personal = Library();
        var entry = (await TemplateStore.CreateAsync(personal, Manifest())).Entry!;

        var first = (await TemplateStore.DuplicateAsync(entry, personal)).Entry!;
        var second = (await TemplateStore.DuplicateAsync(entry, personal)).Entry!;

        Assert.Equal(("console-app-copy", "Console App (copy)"), (first.Id, first.Manifest!.Name));
        Assert.Equal("console-app-copy-2", second.Id);
    }

    [Fact]
    public async Task SameIdInTwoLibraries_IsFine()
    {
        var personal = Library();
        var team = Library("team", LibrarySource.Extra);
        await TemplateStore.CreateAsync(personal, Manifest());
        await TemplateStore.CreateAsync(team, Manifest());

        var all = (await TemplateStore.ListAsync(personal)).Concat(await TemplateStore.ListAsync(team)).ToList();

        Assert.Equal(["Personal/console-app", "team/console-app"], all.Select(e => e.Reference));
    }

    [Fact]
    public void Libraries_PersonalFirst_ExtrasDeduplicated_BuiltInLast()
    {
        var extra = Path.Combine(_root, "team-templates");
        var settings = new TemplatesSettings { ExtraLibraries = [extra, extra + "\\", "", Path.Combine(_root, "DevHub", "templates")] };

        var libraries = TemplateLibraries.Resolve(settings, _root, Path.Combine(_root, "builtin"));

        Assert.Equal(["Personal", "team-templates", "Built-in"], libraries.Select(l => l.Name));
        Assert.Equal(TemplateLibraries.DefaultPersonalRoot(_root), libraries[0].Root);
        Assert.Equal([false, false, true], libraries.Select(l => l.IsReadOnly));
    }

    [Fact]
    public void Libraries_PersonalCanBeMoved()
    {
        var moved = Path.Combine(_root, "my-templates");

        var libraries = TemplateLibraries.Resolve(new TemplatesSettings { PersonalLibrary = moved }, _root, _root);

        Assert.Equal(moved, libraries[0].Root);
    }
}
