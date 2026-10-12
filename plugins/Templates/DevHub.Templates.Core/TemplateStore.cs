namespace DevHub.Templates.Core;

/// <summary>
/// A template as found in a library. A template whose manifest can't be read or is invalid is
/// still listed (with its <see cref="Problems"/>), so the browser can show it as broken instead of
/// it silently disappearing.
/// </summary>
/// <param name="Folder">The template's folder (<c>&lt;library&gt;/&lt;id&gt;</c>).</param>
/// <param name="Manifest">Null when template.json couldn't be read.</param>
public sealed record TemplateEntry(TemplateLibrary Library, string Folder, TemplateManifest? Manifest, IReadOnlyList<string> Problems)
{
    /// <summary>The folder name (equal to the manifest id when all is well).</summary>
    public string Id => Path.GetFileName(Folder);

    /// <summary>"Personal/unity-game": unique across libraries.</summary>
    public string Reference => $"{Library.Name}/{Id}";

    /// <summary>The <c>content/</c> folder (may not exist: a raw template needs none).</summary>
    public string ContentFolder => Path.Combine(Folder, TemplateStore.ContentFolderName);

    public bool HasContent => Directory.Exists(ContentFolder) && Directory.EnumerateFileSystemEntries(ContentFolder).Any();

    /// <summary>Readable and valid: can be used.</summary>
    public bool IsUsable => Manifest is not null && Problems.Count == 0;
}

/// <summary>The outcome of a change to a library: the template as it is now, or why nothing changed.</summary>
public sealed record TemplateStoreResult(TemplateEntry? Entry, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;

    public static TemplateStoreResult Failed(params string[] errors) => new(null, errors);
}

/// <summary>
/// Reads and changes the templates in libraries (§2): list, create, save, delete, duplicate.
/// User mistakes (an invalid manifest, a read-only library, a taken id) come back as errors in
/// the result; only unexpected I/O failures throw.
/// </summary>
public static class TemplateStore
{
    public const string ManifestFileName = "template.json";
    public const string ContentFolderName = "content";

    /// <summary>Every template in the library, by id. A missing library folder is just empty.</summary>
    public static async Task<IReadOnlyList<TemplateEntry>> ListAsync(TemplateLibrary library, CancellationToken ct = default)
    {
        if (!Directory.Exists(library.Root))
            return [];

        var entries = new List<TemplateEntry>();
        foreach (var folder in Directory.EnumerateDirectories(library.Root).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(folder, ManifestFileName)))
                entries.Add(await LoadAsync(library, folder, ct));
        }
        return entries;
    }

    /// <summary>Reads one template folder (problems included).</summary>
    public static async Task<TemplateEntry> LoadAsync(TemplateLibrary library, string folder, CancellationToken ct = default)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(Path.Combine(folder, ManifestFileName), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TemplateEntry(library, folder, null, ["Couldn't read template.json: " + ex.Message]);
        }

        var read = ManifestSerializer.Read(json);
        if (read.Manifest is not { } manifest)
            return new TemplateEntry(library, folder, null, [read.Error!]);

        var problems = ManifestValidator.Validate(manifest).Select(i => i.ToString()).ToList();
        var folderName = Path.GetFileName(folder);
        if (manifest.Id != folderName)
            problems.Add($"id: \"{manifest.Id}\" doesn't match the folder name \"{folderName}\".");
        return new TemplateEntry(library, folder, manifest, problems);
    }

    /// <summary>Creates a new template (an empty <c>content/</c> folder and its template.json).</summary>
    public static async Task<TemplateStoreResult> CreateAsync(TemplateLibrary library, TemplateManifest manifest, CancellationToken ct = default)
    {
        if (CheckWritable(library) is { } readOnly)
            return readOnly;
        if (Invalid(manifest) is { } invalid)
            return invalid;

        var folder = Path.Combine(library.Root, manifest.Id);
        if (Directory.Exists(folder))
            return TemplateStoreResult.Failed($"\"{library.Name}\" already has a template \"{manifest.Id}\".");

        Directory.CreateDirectory(Path.Combine(folder, ContentFolderName));
        await WriteManifestAsync(folder, manifest, ct);
        return new TemplateStoreResult(await LoadAsync(library, folder, ct), []);
    }

    /// <summary>
    /// Saves a changed manifest. A changed id renames the folder (refused if that id is taken).
    /// </summary>
    public static async Task<TemplateStoreResult> SaveAsync(TemplateEntry entry, TemplateManifest manifest, CancellationToken ct = default)
    {
        if (CheckWritable(entry.Library) is { } readOnly)
            return readOnly;
        if (Invalid(manifest) is { } invalid)
            return invalid;

        var folder = entry.Folder;
        if (manifest.Id != entry.Id)
        {
            var renamed = Path.Combine(entry.Library.Root, manifest.Id);
            if (Directory.Exists(renamed))
                return TemplateStoreResult.Failed($"\"{entry.Library.Name}\" already has a template \"{manifest.Id}\".");
            Directory.Move(folder, renamed);
            folder = renamed;
        }

        await WriteManifestAsync(folder, manifest, ct);
        return new TemplateStoreResult(await LoadAsync(entry.Library, folder, ct), []);
    }

    /// <summary>Deletes the template's folder (manifest and content).</summary>
    public static TemplateStoreResult Delete(TemplateEntry entry)
    {
        if (CheckWritable(entry.Library) is { } readOnly)
            return readOnly;
        if (Directory.Exists(entry.Folder))
            Directory.Delete(entry.Folder, recursive: true);
        return new TemplateStoreResult(null, []);
    }

    /// <summary>
    /// Copies a template (e.g. a built-in) into a writable library. Keeps the id when it's free
    /// there, else adds "-copy" ("-copy-2", …); a copy in the same library gets " (copy)" in its name.
    /// Works for a broken template too (to repair it), as long as its manifest can be read.
    /// </summary>
    public static async Task<TemplateStoreResult> DuplicateAsync(TemplateEntry entry, TemplateLibrary target, CancellationToken ct = default)
    {
        if (CheckWritable(target) is { } readOnly)
            return readOnly;
        if (entry.Manifest is not { } manifest)
            return TemplateStoreResult.Failed("This template's template.json can't be read, so it can't be duplicated.");

        var id = manifest.Id;
        for (var n = 1; Directory.Exists(Path.Combine(target.Root, id)); n++)
            id = n == 1 ? $"{manifest.Id}-copy" : $"{manifest.Id}-copy-{n}";

        var folder = Path.Combine(target.Root, id);
        CopyFolder(entry.Folder, folder);
        var copy = manifest with
        {
            Id = id,
            Name = target == entry.Library ? manifest.Name + " (copy)" : manifest.Name,
        };
        await WriteManifestAsync(folder, copy, ct);
        return new TemplateStoreResult(await LoadAsync(target, folder, ct), []);
    }

    private static TemplateStoreResult? CheckWritable(TemplateLibrary library) =>
        library.IsReadOnly
            ? TemplateStoreResult.Failed($"\"{library.Name}\" is read-only: duplicate the template into your own library to change it.")
            : null;

    private static TemplateStoreResult? Invalid(TemplateManifest manifest) =>
        ManifestValidator.Validate(manifest) is { Count: > 0 } issues
            ? new TemplateStoreResult(null, issues.Select(i => i.ToString()).ToList())
            : null;

    /// <summary>Writes template.json through a temporary file, so a crash never leaves half a manifest.</summary>
    private static async Task WriteManifestAsync(string folder, TemplateManifest manifest, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, ManifestFileName);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, ManifestSerializer.Write(manifest), ct);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Copies a folder tree. Links (symlinks / junctions) are skipped, never followed out of the tree.</summary>
    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in new DirectoryInfo(from).EnumerateFiles())
        {
            if (file.LinkTarget is null)
                file.CopyTo(Path.Combine(to, file.Name));
        }
        foreach (var dir in new DirectoryInfo(from).EnumerateDirectories())
        {
            if (dir.LinkTarget is null)
                CopyFolder(dir.FullName, Path.Combine(to, dir.Name));
        }
    }
}
