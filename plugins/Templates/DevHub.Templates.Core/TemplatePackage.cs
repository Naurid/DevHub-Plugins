using System.IO.Compression;
using DevHub.Templates.Core.Editing;

namespace DevHub.Templates.Core;

/// <summary>
/// A template as one .zip, to share it (§10 sharing): the template's folder (template.json and
/// content/) under one top folder named after its id. Importing one checks the manifest, never
/// writes outside the new template's folder (an entry with "..", or an absolute path, is refused:
/// "zip slip"), and takes a free id in the target library ("-2"…) when the id is taken.
/// </summary>
public static class TemplatePackage
{
    public static async Task ExportAsync(TemplateEntry template, string zipPath, CancellationToken ct = default)
    {
        var temp = zipPath + ".tmp";
        await using (var stream = File.Create(temp))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(template.Folder, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (new FileInfo(file).LinkTarget is not null)
                    continue;
                var name = template.Id + "/" + Path.GetRelativePath(template.Folder, file).Replace('\\', '/');
                zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
            }
        }
        File.Move(temp, zipPath, overwrite: true);
    }

    /// <summary>Unpacks a template .zip into <paramref name="library"/>.</summary>
    public static async Task<TemplateStoreResult> ImportAsync(string zipPath, TemplateLibrary library, CancellationToken ct = default)
    {
        if (library.IsReadOnly)
            return TemplateStoreResult.Failed($"\"{library.Name}\" is read-only.");

        using var zip = ZipFile.OpenRead(zipPath);
        var manifests = zip.Entries.Where(e => e.Name == TemplateStore.ManifestFileName).ToList();
        // template.json at the top, or in the one top folder.
        var manifestEntry = manifests.FirstOrDefault(e => e.FullName.Replace('\\', '/').Count(c => c == '/') <= 1);
        if (manifestEntry is null || manifests.Count(e => e.FullName.Replace('\\', '/').Count(c => c == '/') <= 1) > 1)
            return TemplateStoreResult.Failed("The zip doesn't hold one template (a template.json at its top or in its one top folder).");
        var prefix = manifestEntry.FullName[..^TemplateStore.ManifestFileName.Length];

        string json;
        await using (var stream = manifestEntry.Open())
        using (var reader = new StreamReader(stream))
            json = await reader.ReadToEndAsync(ct);
        var (manifest, problems) = ManifestText.Parse(json);
        if (manifest is null || problems.Count > 0)
            return TemplateStoreResult.Failed(["The template.json in the zip can't be used:", .. problems]);

        var id = TemplateIds.Unique(library, manifest.Id);
        var folder = Path.Combine(library.Root, id);
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal)))
            {
                ct.ThrowIfCancellationRequested();
                var relative = entry.FullName[prefix.Length..].Replace('\\', '/');
                if (relative.Length == 0)
                    continue;
                var target = Path.GetFullPath(Path.Combine(folder, relative));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"\"{entry.FullName}\" would be written outside the template.");
                if (relative.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
        }
        catch (InvalidDataException ex)
        {
            Directory.Delete(folder, recursive: true);
            return TemplateStoreResult.Failed(ex.Message);
        }
        catch
        {
            Directory.Delete(folder, recursive: true);
            throw;
        }

        // The folder name is the id: write it into the manifest when a free one had to be taken.
        var entryNow = await TemplateStore.LoadAsync(library, folder, ct);
        if (id != manifest.Id)
            return await TemplateStore.SaveAsync(entryNow with { Folder = folder }, manifest with { Id = id }, ct);
        return new TemplateStoreResult(entryNow, []);
    }
}
