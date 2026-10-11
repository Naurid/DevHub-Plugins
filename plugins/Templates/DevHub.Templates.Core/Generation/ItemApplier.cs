using DevHub.Templates.Core.Rendering;

namespace DevHub.Templates.Core.Generation;

/// <summary>
/// Applies an item template to an existing folder (§7). Staging scans the folder into a
/// <see cref="FileTree"/> (existing entries), renders the content into a staging folder on top
/// (a file that already exists is a <see cref="StagedItem.Conflicts">conflict</see>) and runs the
/// steps, which may move the folder's own files. Nothing in the folder changes until
/// <see cref="StagedItem.ApplyAsync"/>.
/// </summary>
public static class ItemApplier
{
    /// <summary>Folders not scanned (not shown, not moved by steps): version control and big generated folders.</summary>
    public static readonly IReadOnlyCollection<string> SkippedFolders =
        [".git", ".vs", ".idea", "node_modules", "Library", "Temp", "obj", "bin"];

    public static async Task<StageResult<StagedItem>> StageAsync(TemplateEntry template, RenderContext context, string targetFolder,
        CancellationToken ct = default)
    {
        if (template is not { IsUsable: true, Manifest: { Kind: TemplateKind.Item } manifest })
            return new StageResult<StagedItem>(null, template.Problems.Count > 0 ? [.. template.Problems] : ["This isn't an item template."]);
        if (!Directory.Exists(targetFolder))
            return new StageResult<StagedItem>(null, [$"The folder {targetFolder} doesn't exist."]);

        var tree = FileTree.Scan(targetFolder, EntryOrigin.Existing, SkippedFolders);
        var staging = Staging.Create(targetFolder);
        var item = new StagedItem(staging, targetFolder, tree);
        try
        {
            var error = await Staging.OverlayAsync(template, manifest, context, tree, Path.Combine(staging, "content"), ct,
                            (path, replaced) => item.AddConflict(path, replaced))
                        ?? Staging.ApplySteps(manifest, context, tree);
            if (error is not null)
            {
                item.Dispose();
                return new StageResult<StagedItem>(null, [error]);
            }
            return new StageResult<StagedItem>(item, []);
        }
        catch
        {
            item.Dispose();
            throw;
        }
    }
}

/// <summary>
/// An item template waiting to be applied: <see cref="Tree"/> is the folder as it will be,
/// <see cref="Conflicts"/> the existing files the template would overwrite.
/// </summary>
public sealed class StagedItem : IStagedResult
{
    private readonly List<TemplateStep> _edits = [];
    private readonly Dictionary<string, TreeEntry> _conflicts = new(StringComparer.OrdinalIgnoreCase);

    internal StagedItem(string stagingFolder, string targetFolder, FileTree tree)
    {
        StagingFolder = stagingFolder;
        TargetFolder = targetFolder;
        Tree = tree;
    }

    public string StagingFolder { get; }
    public string TargetFolder { get; }

    /// <summary>The folder the files go into (same as <see cref="TargetFolder"/>).</summary>
    public string TargetPath => TargetFolder;

    /// <summary>The target folder as it will be (existing entries, the template's, moves applied).</summary>
    public FileTree Tree { get; }

    /// <summary>Paths where the template would replace a file that's already there (the user decides per file).</summary>
    public IReadOnlyCollection<string> Conflicts => _conflicts.Keys;

    public IReadOnlyList<TemplateStep> Edits => _edits;

    internal void AddConflict(string path, TreeEntry replaced)
    {
        if (replaced.Origin == EntryOrigin.Existing)
            _conflicts[path] = replaced;
    }

    /// <summary>A preview edit, like <see cref="StagedProject.ApplyEdit"/>.</summary>
    public string? ApplyEdit(TemplateStep edit) => Staging.ApplyEdit(Tree, edit, _edits);

    /// <summary>
    /// Changes the folder: existing files deleted / moved by the steps, the template's files written
    /// (a conflicting one only when its path is in <paramref name="overwrite"/>; else the existing file
    /// stays), new folders created, folders left empty by moves removed. Then the staging folder goes.
    /// </summary>
    public Task<PlaceResult> ApplyAsync(IReadOnlySet<string> overwrite, CancellationToken ct = default)
    {
        var target = TargetFolder;
        string Full(string relative) => SafePath.Resolve(target, relative)
                                        ?? throw new InvalidOperationException($"{relative} leaves {target}.");

        // 1. Existing entries a step deleted (not the ones a template file replaced: those are conflicts).
        foreach (var removed in Tree.Removed.Where(r => r.Origin == EntryOrigin.Existing && !_conflicts.ContainsKey(r.Path)))
        {
            var path = Full(removed.OriginalPath);
            if (removed.IsFolder && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (!removed.IsFolder && File.Exists(path))
                File.Delete(path);
        }

        // 2. Existing files that moved: first out of the way (so A→B, B→C can't collide), then into place.
        var hold = Path.Combine(StagingFolder, "moving");
        Directory.CreateDirectory(hold);
        var moving = Tree.Entries.Where(e => e is { Origin: EntryOrigin.Existing, IsFolder: false, IsMoved: true })
            .Select((e, i) => (Entry: e, Held: Path.Combine(hold, i.ToString())))
            .ToList();
        foreach (var (entry, held) in moving)
            File.Move(Full(entry.OriginalPath), held);

        // 3. Folders of the result (new, and the destinations of moves).
        foreach (var folder in Tree.Entries.Where(e => e.IsFolder))
            Directory.CreateDirectory(Full(folder.Path));

        foreach (var (entry, held) in moving)
            File.Move(held, Full(entry.Path));

        // 4. The template's files.
        foreach (var file in Tree.Entries.Where(e => e is { Origin: EntryOrigin.Template, IsFolder: false }))
        {
            ct.ThrowIfCancellationRequested();
            if (_conflicts.ContainsKey(file.Path) && !overwrite.Contains(file.Path))
                continue;
            File.Copy(file.Source!, Full(file.Path), overwrite: true);
        }

        // 5. Folders that moves left empty (and that aren't part of the result).
        // Candidates: the original places of moved folders, and every folder a moved file came from
        // (with its parents). Only those not in the result and now empty are removed, deepest first.
        var resultFolders = Tree.Entries.Where(e => e.IsFolder).Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var movedFolder in Tree.Entries.Where(e => e is { Origin: EntryOrigin.Existing, IsFolder: true, IsMoved: true }))
            candidates.Add(movedFolder.OriginalPath);
        foreach (var (entry, _) in moving)
        {
            for (var p = entry.OriginalPath; p.LastIndexOf('/') is var s and >= 0; p = p[..s])
                candidates.Add(p[..s]);
        }
        foreach (var folder in candidates.Where(f => !resultFolders.Contains(f)).OrderByDescending(f => f.Length))
        {
            var path = Full(folder);
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }

        Dispose();
        return Task.FromResult(new PlaceResult(target, null));
    }

    /// <summary>
    /// What applying will do to the folder, for the preview: the template's new files, the files it
    /// would overwrite (only those in <paramref name="overwrite"/>; the others stay as they are), your
    /// files that move, and your files that get deleted (a step or an edit deleted them).
    /// </summary>
    public ItemChanges Changes(IReadOnlySet<string> overwrite)
    {
        var files = Tree.Entries.Where(e => !e.IsFolder).ToList();
        return new ItemChanges(
            files.Where(e => e.Origin == EntryOrigin.Template && !_conflicts.ContainsKey(e.Path)).Select(e => e.Path).ToList(),
            _conflicts.Keys.Where(overwrite.Contains).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            _conflicts.Keys.Where(c => !overwrite.Contains(c)).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            files.Where(e => e is { Origin: EntryOrigin.Existing, IsMoved: true }).Select(e => (e.OriginalPath, e.Path)).ToList(),
            Tree.Removed.Where(r => r is { Origin: EntryOrigin.Existing, IsFolder: false } && !_conflicts.ContainsKey(r.Path))
                .Select(r => r.OriginalPath).Order(StringComparer.OrdinalIgnoreCase).ToList());
    }

    public void Dispose() => Staging.Delete(StagingFolder);
}

/// <summary>What applying an item template does to the folder (see <see cref="StagedItem.Changes"/>).</summary>
/// <param name="Kept">Conflicts not overwritten: your file stays, the template's isn't written.</param>
public sealed record ItemChanges(IReadOnlyList<string> Added, IReadOnlyList<string> Overwritten, IReadOnlyList<string> Kept,
    IReadOnlyList<(string From, string To)> Moved, IReadOnlyList<string> Deleted)
{
    public bool IsEmpty => Added.Count == 0 && Overwritten.Count == 0 && Moved.Count == 0 && Deleted.Count == 0;
}
