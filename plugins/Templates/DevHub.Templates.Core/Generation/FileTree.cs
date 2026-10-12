using DevHub.Templates.Core.Rendering;

namespace DevHub.Templates.Core.Generation;

/// <summary>Where an entry of the result came from (the preview labels them).</summary>
public enum EntryOrigin
{
    /// <summary>Made by the base command.</summary>
    Command,

    /// <summary>From the template's content (or a folder a step / an edit created).</summary>
    Template,

    /// <summary>Already in the folder an item template is applied to.</summary>
    Existing,
}

/// <summary>
/// One file or folder of the result. <see cref="Source"/> is where the file's bytes are now (in
/// staging, or the existing file); <see cref="OriginalPath"/> is where it started, so a moved
/// entry can be shown as moved and an existing file can be moved on disk.
/// </summary>
public sealed record TreeEntry(string Path, bool IsFolder, EntryOrigin Origin, string? Source, string OriginalPath)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>The folder it's in ("" at the root).</summary>
    public string Parent => Path.LastIndexOf('/') is var slash and >= 0 ? Path[..slash] : "";

    public bool IsMoved => !Path.Equals(OriginalPath, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => IsFolder ? Path + "/" : Path;
}

/// <summary>
/// The result of a generation as a set of paths, before anything is written where the user
/// will see it. Steps (§6) and preview edits change this tree, not the disk: moves only change
/// paths, so they're instant, can be previewed, and an item template can "move" a user's files
/// without touching them until the user agrees. Paths are "/"-separated, relative to the root,
/// compared case-insensitively (Windows).
/// </summary>
public sealed class FileTree
{
    private readonly Dictionary<string, TreeEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TreeEntry> _removed = [];

    /// <summary>The tree as it is now, to go back to (<see cref="Restore"/>): undo of a preview edit.</summary>
    public Snapshot TakeSnapshot() => new(new Dictionary<string, TreeEntry>(_entries, StringComparer.OrdinalIgnoreCase), [.. _removed]);

    /// <summary>Puts the tree back the way it was at <paramref name="snapshot"/>.</summary>
    public void Restore(Snapshot snapshot)
    {
        _entries.Clear();
        foreach (var (path, entry) in snapshot.Entries)
            _entries[path] = entry;
        _removed.Clear();
        _removed.AddRange(snapshot.Removed);
    }

    /// <summary>A copy of a tree's state (entries are immutable records, so copying the collections is enough).</summary>
    public sealed record Snapshot(IReadOnlyDictionary<string, TreeEntry> Entries, IReadOnlyList<TreeEntry> Removed);

    /// <summary>Every entry, sorted by path.</summary>
    public IReadOnlyList<TreeEntry> Entries =>
        _entries.Values.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Entries that were deleted by a step or edit, or replaced by a template file.</summary>
    public IReadOnlyList<TreeEntry> Removed => _removed;

    public TreeEntry? this[string path] => _entries.GetValueOrDefault(path.TrimEnd('/'));

    /// <summary>
    /// The tree of what's in a folder on disk. Links (symlinks / junctions) aren't followed;
    /// folders named in <paramref name="skip"/> (e.g. ".git") are left out with everything in them.
    /// </summary>
    public static FileTree Scan(string root, EntryOrigin origin, IReadOnlyCollection<string>? skip = null)
    {
        var tree = new FileTree();
        if (!Directory.Exists(root))
            return tree;

        void Walk(DirectoryInfo folder, string relative)
        {
            foreach (var dir in folder.EnumerateDirectories())
            {
                if (dir.LinkTarget is not null || skip?.Contains(dir.Name, StringComparer.OrdinalIgnoreCase) == true)
                    continue;
                var path = relative.Length == 0 ? dir.Name : relative + "/" + dir.Name;
                tree._entries[path] = new TreeEntry(path, true, origin, dir.FullName, path);
                Walk(dir, path);
            }
            foreach (var file in folder.EnumerateFiles())
            {
                if (file.LinkTarget is not null)
                    continue;
                var path = relative.Length == 0 ? file.Name : relative + "/" + file.Name;
                tree._entries[path] = new TreeEntry(path, false, origin, file.FullName, path);
            }
        }

        Walk(new DirectoryInfo(root), "");
        return tree;
    }

    /// <summary>Adds a folder and its missing parents.</summary>
    public void AddFolder(string path, EntryOrigin origin)
    {
        path = path.Trim('/');
        if (path.Length == 0)
            return;
        var parts = path.Split('/');
        for (var i = 1; i <= parts.Length; i++)
        {
            var folder = string.Join('/', parts[..i]);
            _entries.TryAdd(folder, new TreeEntry(folder, true, origin, null, folder));
        }
    }

    /// <summary>
    /// Adds a file (its parents too). An existing file at that path is replaced and returned
    /// (the template's version wins over the command's; for an item it's a conflict).
    /// </summary>
    /// <returns>The replaced entry, or null.</returns>
    public TreeEntry? AddFile(string path, EntryOrigin origin, string source)
    {
        var parent = path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : "";
        AddFolder(parent, origin);
        var replaced = _entries.GetValueOrDefault(path);
        if (replaced is { IsFolder: true })
            throw new InvalidOperationException($"{path} is a folder; a file can't replace it.");
        if (replaced is not null)
            _removed.Add(replaced);
        _entries[path] = new TreeEntry(path, false, origin, source, path);
        return replaced;
    }

    /// <summary>Runs one step (§6) on the tree. Returns why it can't run (and changes nothing then), or null.</summary>
    public string? Apply(RenderedStep step)
    {
        if (step.Error is { } renderError)
            return renderError;

        switch (step.Op)
        {
            case StepOp.CreateFolder:
                if (Glob.HasWildcards(step.Path!))
                    return $"Create folder \"{step.Path}\": a folder name can't contain * or ?.";
                if (this[step.Path!] is { IsFolder: false })
                    return $"Create folder \"{step.Path}\": there's a file with that name.";
                AddFolder(step.Path!, EntryOrigin.Template);
                return null;

            case StepOp.Delete:
            {
                if (Match(step.Path!, out var error) is not { } matches)
                    return $"Delete: {error}";
                foreach (var entry in matches)
                    Remove(entry);
                return null;
            }

            case StepOp.Rename:
            {
                if (Glob.HasWildcards(step.From!))
                    return $"Rename \"{step.From}\": rename takes one path (use move for many).";
                if (this[step.From!] is not { } entry)
                    return $"Rename: \"{step.From}\" doesn't exist.";
                var target = entry.Parent.Length == 0 ? step.To! : entry.Parent + "/" + step.To;
                return MoveTo(entry, target, $"Rename \"{step.From}\"");
            }

            case StepOp.Move:
            {
                if (Match(step.From!, out var error) is not { } matches)
                    return $"Move: {error}";
                if (step.To!.EndsWith('/'))
                    return MoveInto(matches, step.To.TrimEnd('/'), step.From!);
                return matches.Count switch
                {
                    0 => null,
                    1 => MoveTo(matches[0], step.To, $"Move \"{step.From}\""),
                    _ => $"Move \"{step.From}\": it matches {matches.Count} items, so \"{step.To}\" must be a folder (end it with /).",
                };
            }

            default:
                return $"Unknown step {step.Op}.";
        }
    }

    /// <summary>
    /// The entries a path or glob means: a plain path must exist; a glob may match nothing.
    /// Entries inside a matched folder are left out (moving the folder moves them).
    /// </summary>
    private List<TreeEntry>? Match(string pattern, out string? error)
    {
        error = null;
        if (!Glob.HasWildcards(pattern))
        {
            if (this[pattern] is { } entry)
                return [entry];
            error = $"\"{pattern}\" doesn't exist.";
            return null;
        }

        var glob = new Glob(pattern);
        var matches = _entries.Values.Where(e => glob.IsMatch(e.Path)).ToList();
        var folders = matches.Where(m => m.IsFolder).Select(m => m.Path + "/").ToList();
        return matches
            .Where(m => !folders.Any(f => m.Path.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string? MoveInto(List<TreeEntry> entries, string folder, string from)
    {
        if (this[folder] is { IsFolder: false })
            return $"Move \"{from}\": \"{folder}\" is a file, not a folder.";

        // Check everything first, so a failing step changes nothing.
        var moves = new List<(TreeEntry Entry, string Target)>();
        foreach (var entry in entries)
        {
            var target = folder.Length == 0 ? entry.Name : folder + "/" + entry.Name;
            if (target.Equals(entry.Path, StringComparison.OrdinalIgnoreCase))
                continue; // already there
            if (entry.IsFolder && (folder + "/").StartsWith(entry.Path + "/", StringComparison.OrdinalIgnoreCase))
                return $"Move \"{from}\": can't move the folder \"{entry.Path}\" into itself.";
            if (this[target] is not null)
                return $"Move \"{from}\": \"{target}\" already exists.";
            moves.Add((entry, target));
        }

        if (entries.Count == 0)
            return null; // a glob that matched nothing: no folder either
        AddFolder(folder, EntryOrigin.Template);
        foreach (var (entry, target) in moves)
            Relocate(entry, target);
        return null;
    }

    private string? MoveTo(TreeEntry entry, string target, string what)
    {
        if (target.Equals(entry.Path, StringComparison.OrdinalIgnoreCase) && target == entry.Path)
            return null;
        if (entry.IsFolder && target.StartsWith(entry.Path + "/", StringComparison.OrdinalIgnoreCase))
            return $"{what}: can't move a folder into itself.";
        // A case-only rename (readme.md → README.md) is allowed: it's the same entry.
        if (this[target] is { } existing && existing != entry)
            return $"{what}: \"{target}\" already exists.";
        if (target.LastIndexOf('/') is var slash and >= 0 && this[target[..slash]] is { IsFolder: false })
            return $"{what}: \"{target[..slash]}\" is a file, not a folder.";

        AddFolder(target.LastIndexOf('/') is var s and >= 0 ? target[..s] : "", EntryOrigin.Template);
        Relocate(entry, target);
        return null;
    }

    /// <summary>
    /// Changes an entry's path; for a folder, everything in it moves along. Folders the move
    /// leaves empty go away (they're not "removed": nothing in them is deleted, an item's
    /// apply just doesn't keep the empty folder).
    /// </summary>
    private void Relocate(TreeEntry entry, string target)
    {
        RelocateOnly(entry, target);
        for (var parent = entry.Parent; parent.Length > 0; parent = parent.LastIndexOf('/') is var s and >= 0 ? parent[..s] : "")
        {
            var prefix = parent + "/";
            if (_entries.Keys.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                break;
            _entries.Remove(parent);
        }
    }

    private void RelocateOnly(TreeEntry entry, string target)
    {
        var prefix = entry.Path + "/";
        var moving = entry.IsFolder
            ? _entries.Values.Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Append(entry).ToList()
            : [entry];
        foreach (var e in moving)
            _entries.Remove(e.Path);
        foreach (var e in moving)
        {
            var newPath = e == entry ? target : target + "/" + e.Path[prefix.Length..];
            _entries[newPath] = e with { Path = newPath };
        }
    }

    private void Remove(TreeEntry entry)
    {
        var prefix = entry.Path + "/";
        var removing = entry.IsFolder
            ? _entries.Values.Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Append(entry).ToList()
            : [entry];
        foreach (var e in removing)
        {
            _entries.Remove(e.Path);
            _removed.Add(e);
        }
    }
}

/// <summary>
/// The preview's edits, in order, with the tree as it was before each one, so the last edit can be
/// undone exactly (a delete brings its files back, a move puts them where they were).
/// </summary>
public sealed class EditHistory
{
    private readonly List<TemplateStep> _edits = [];
    private readonly List<FileTree.Snapshot> _before = [];

    public IReadOnlyList<TemplateStep> Edits => _edits;
    public bool CanUndo => _edits.Count > 0;

    /// <summary>Applies an edit (checked like a step); kept, with a way back, when it worked. Returns why not, or null.</summary>
    public string? Apply(FileTree tree, TemplateStep edit)
    {
        var before = tree.TakeSnapshot();
        if (Staging.ApplyEdit(tree, edit, _edits) is { } error)
            return error;
        _before.Add(before);
        return null;
    }

    /// <summary>Takes back the last edit; returns it, or null when there's none.</summary>
    public TemplateStep? Undo(FileTree tree)
    {
        if (_edits.Count == 0)
            return null;
        var edit = _edits[^1];
        tree.Restore(_before[^1]);
        _edits.RemoveAt(_edits.Count - 1);
        _before.RemoveAt(_before.Count - 1);
        return edit;
    }
}
