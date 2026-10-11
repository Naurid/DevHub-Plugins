using DevHub.Templates.Core.Commands;
using DevHub.Templates.Core.Rendering;

namespace DevHub.Templates.Core.Generation;

/// <summary>What the generation is doing now (for the status bar / progress view), or a line of command output.</summary>
public sealed record GenerationProgress(string Step, CommandOutputLine? Output = null);

/// <summary>The outcome of staging: the staged result to preview, or why it stopped (nothing was written to the target).</summary>
/// <param name="CommandResult">The base command's run (its output for the error view), when there was one.</param>
public sealed record StageResult<T>(T? Staged, IReadOnlyList<string> Errors, CommandResult? CommandResult = null) where T : class
{
    public bool Succeeded => Staged is not null;
}

/// <summary>The outcome of placing a project or applying an item.</summary>
/// <param name="Path">Where the result is now (the project folder / the target folder).</param>
public sealed record PlaceResult(string? Path, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Generates a project (§4): stages it (base command → overlay → steps) for the preview, then
/// places it. The confirmation of the commands (§4) is the caller's: call <see cref="StageAsync"/>
/// only after the user agreed to <see cref="TemplateRendering.AllCommands"/>.
/// </summary>
public sealed class ProjectGenerator(CommandRunner runner)
{
    /// <summary>
    /// Steps 1–3 of §4 in a staging folder next to the target (same drive, so placing is a move).
    /// On any failure the staging folder is deleted and the errors returned.
    /// </summary>
    /// <param name="parentFolder">Where the project folder will go (must exist).</param>
    /// <param name="raw">
    /// A raw project (§4): only the base command; the template's content and steps are skipped
    /// (and the caller skips the post-create commands, see <see cref="TemplateRendering.AllCommands"/>).
    /// </param>
    public async Task<StageResult<StagedProject>> StageAsync(TemplateEntry template, RenderContext context, string parentFolder,
        bool raw = false, IProgress<GenerationProgress>? progress = null, CancellationToken ct = default)
    {
        if (template is not { IsUsable: true, Manifest: { Kind: TemplateKind.Project } manifest })
            return Fail(template.Problems.Count > 0 ? [.. template.Problems] : ["This isn't a project template."]);
        if (raw && manifest.BaseCommand is null)
            return Fail("This template has no base command, so there's no raw project to make.");

        var root = TargetFor(manifest, context, parentFolder);
        if (root.Error is { } targetError)
            return Fail(targetError);
        var rootName = root.Path!;

        var staging = Staging.Create(parentFolder);
        var project = new StagedProject(staging, parentFolder, rootName);
        try
        {
            // 1. Base command, in the (empty) root or in its parent.
            var work = Path.Combine(staging, "work");
            var workRoot = Path.Combine(work, rootName);
            Directory.CreateDirectory(work);
            CommandResult? commandResult = null;
            if (TemplateRendering.BaseCommand(manifest, context) is { } command)
            {
                var runIn = manifest.BaseCommand!.RunIn == RunIn.Root ? workRoot : work;
                Directory.CreateDirectory(runIn);
                progress?.Report(new GenerationProgress($"Running {command.DisplayText}"));
                var output = progress is null ? null : new InlineProgress<CommandOutputLine>(l => progress.Report(new GenerationProgress("", l)));
                commandResult = await runner.RunAsync(command, runIn, output, ct);
                if (!commandResult.Succeeded)
                {
                    project.Dispose();
                    return new StageResult<StagedProject>(null, [commandResult.Error!], commandResult);
                }
                if (!Directory.Exists(workRoot))
                {
                    project.Dispose();
                    return new StageResult<StagedProject>(null,
                        [$"The base command didn't create the folder \"{rootName}\" (with runIn \"parent\" it must create the project folder itself)."],
                        commandResult);
                }
                project.Tree = FileTree.Scan(workRoot, EntryOrigin.Command);
            }

            if (raw)
                return new StageResult<StagedProject>(project, [], commandResult);

            // 2. The template's content on top.
            progress?.Report(new GenerationProgress("Copying the template's files"));
            if (await Staging.OverlayAsync(template, manifest, context, project.Tree, Path.Combine(staging, "content"), ct) is { } overlayError)
            {
                project.Dispose();
                return new StageResult<StagedProject>(null, [overlayError], commandResult);
            }

            // 3. Steps.
            progress?.Report(new GenerationProgress("Arranging the files"));
            if (Staging.ApplySteps(manifest, context, project.Tree) is { } stepError)
            {
                project.Dispose();
                return new StageResult<StagedProject>(null, [stepError], commandResult);
            }
            return new StageResult<StagedProject>(project, [], commandResult);
        }
        catch
        {
            project.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The project folder the values make under <paramref name="parentFolder"/>, or why it can't be
    /// used: a bad name, a parent that doesn't exist, a file there, or a folder that isn't empty.
    /// The form checks this before anything runs; placing checks it again.
    /// </summary>
    /// <returns>The folder name in <see cref="RenderedPath.Path"/>, or the error.</returns>
    public static RenderedPath TargetFor(TemplateManifest manifest, RenderContext context, string parentFolder)
    {
        var root = TemplateRendering.RootFolder(manifest, context);
        if (!root.Succeeded)
            return new RenderedPath(null, "Project folder: " + root.Error);
        if (string.IsNullOrWhiteSpace(parentFolder) || !Path.IsPathFullyQualified(parentFolder))
            return new RenderedPath(null, "Choose the folder to create the project in.");
        if (!Directory.Exists(parentFolder))
            return new RenderedPath(null, $"The folder {parentFolder} doesn't exist.");
        return StagedProject.TargetProblem(Path.Combine(parentFolder, root.Path!)) is { } problem
            ? new RenderedPath(null, problem)
            : root;
    }

    private static StageResult<StagedProject> Fail(params string[] errors) => new(null, errors);
}

/// <summary>
/// A generated project waiting in staging: the preview shows <see cref="Tree"/>, edits change it
/// (<see cref="ApplyEdit"/>), <see cref="PlaceAsync"/> writes it into the target, and disposing
/// deletes the staging folder (Cancel).
/// </summary>
public sealed class StagedProject : IStagedResult
{
    private readonly List<TemplateStep> _edits = [];

    internal StagedProject(string stagingFolder, string parentFolder, string rootName)
    {
        StagingFolder = stagingFolder;
        ParentFolder = parentFolder;
        RootName = rootName;
    }

    public string StagingFolder { get; }
    public string ParentFolder { get; }

    /// <summary>The project folder's name (rendered rootFolder).</summary>
    public string RootName { get; }

    /// <summary>Where it'll be placed.</summary>
    public string TargetPath => Path.Combine(ParentFolder, RootName);

    /// <summary>The result, relative to the project folder.</summary>
    public FileTree Tree { get; internal set; } = new();

    /// <summary>The edits made in the preview, as concrete steps (see <see cref="PreviewEdits.ToTemplateSteps"/> to save them).</summary>
    public IReadOnlyList<TemplateStep> Edits => _edits;

    /// <summary>A preview edit (move / rename / delete / new folder, concrete paths). Returns why not, or null.</summary>
    public string? ApplyEdit(TemplateStep edit) => Staging.ApplyEdit(Tree, edit, _edits);

    /// <summary>
    /// Writes the project into the target (§4 step 5): refused (nothing written) when the folder
    /// exists and isn't empty. An existing empty folder is filled. Then the staging folder goes.
    /// </summary>
    public Task<PlaceResult> PlaceAsync(CancellationToken ct = default)
    {
        var target = TargetPath;
        if (TargetProblem(target) is { } problem)
            return Task.FromResult(new PlaceResult(null, problem));

        var final = Path.Combine(StagingFolder, "final", RootName);
        Staging.Materialize(Tree, final, ct);

        if (Directory.Exists(target))
        {
            foreach (var dir in Directory.EnumerateDirectories(final))
                Directory.Move(dir, Path.Combine(target, Path.GetFileName(dir)));
            foreach (var file in Directory.EnumerateFiles(final))
                File.Move(file, Path.Combine(target, Path.GetFileName(file)));
        }
        else
        {
            Directory.Move(final, target);
        }
        Dispose();
        return Task.FromResult(new PlaceResult(target, null));
    }

    public void Dispose() => Staging.Delete(StagingFolder);

    /// <summary>Why a project can't be placed at <paramref name="target"/> (a file, or a folder that isn't empty), or null.</summary>
    internal static string? TargetProblem(string target) =>
        File.Exists(target) ? $"{target} is a file. Pick another name."
        : Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()
            ? $"{target} already exists and isn't empty. Pick another name or folder."
        : null;
}

/// <summary>What project and item generation share: the staging folder, overlay, steps, edits.</summary>
internal static class Staging
{
    /// <summary>The prefix of staging folders (hidden; left-overs of a crash are safe to delete).</summary>
    public const string Prefix = ".devhub-staging-";

    public static string Create(string nextTo)
    {
        var folder = Path.Combine(nextTo, Prefix + Guid.NewGuid().ToString("N")[..8]);
        var info = Directory.CreateDirectory(folder);
        info.Attributes |= FileAttributes.Hidden;
        return folder;
    }

    public static void Delete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still in use (an antivirus scan): the hidden folder is left; harmless.
        }
    }

    /// <summary>Renders the template's content/ into <paramref name="stagingContent"/> and adds it to the tree (§4 step 2).</summary>
    public static async Task<string?> OverlayAsync(TemplateEntry template, TemplateManifest manifest, RenderContext context,
        FileTree tree, string stagingContent, CancellationToken ct, Action<string, TreeEntry>? onReplaced = null)
    {
        if (!Directory.Exists(template.ContentFolder))
            return null;

        foreach (var folder in Directory.EnumerateDirectories(template.ContentFolder, "*", SearchOption.AllDirectories))
        {
            var rendered = PathRenderer.RenderPath(SafePath.Relative(template.ContentFolder, folder), context);
            if (!rendered.Succeeded)
                return "Template folder name: " + rendered.Error;
            if (tree[rendered.Path!] is { IsFolder: false })
                return $"\"{rendered.Path}\" is a file in the result, so the template's folder can't go there.";
            tree.AddFolder(rendered.Path!, EntryOrigin.Template);
        }

        foreach (var file in Directory.EnumerateFiles(template.ContentFolder, "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(file).LinkTarget is not null)
                continue;
            var rendered = PathRenderer.RenderPath(SafePath.Relative(template.ContentFolder, file), context);
            if (!rendered.Succeeded)
                return "Template file name: " + rendered.Error;
            if (tree[rendered.Path!] is { IsFolder: true })
                return $"\"{rendered.Path}\" is a folder in the result, so the template's file can't go there.";

            var staged = SafePath.Resolve(stagingContent, rendered.Path!)!;
            await ContentFiles.CopyAsync(file, staged, context, manifest.BinaryExtensions, ct);
            if (tree.AddFile(rendered.Path!, EntryOrigin.Template, staged) is { } replaced)
                onReplaced?.Invoke(rendered.Path!, replaced);
        }
        return null;
    }

    /// <summary>The template's steps, in order (§4 step 3).</summary>
    public static string? ApplySteps(TemplateManifest manifest, RenderContext context, FileTree tree)
    {
        var steps = TemplateRendering.Steps(manifest, context);
        for (var i = 0; i < steps.Count; i++)
        {
            if (tree.Apply(steps[i]) is { } error)
                return $"Step {i + 1}: {error}";
        }
        return null;
    }

    /// <summary>A preview edit: concrete paths (no placeholders), checked like a step; kept when it worked.</summary>
    public static string? ApplyEdit(FileTree tree, TemplateStep edit, List<TemplateStep> edits)
    {
        foreach (var path in new[] { edit.Path, edit.From, edit.To }.OfType<string>())
        {
            var trimmed = path.TrimEnd('/');
            if (trimmed.Length == 0 || trimmed.Split('/').Any(s => s is "" or "." or ".."))
                return $"\"{path}\" isn't a path inside the project.";
        }
        if (tree.Apply(new RenderedStep(edit.Op, edit.Path, edit.From, edit.To, null)) is { } error)
            return error;
        edits.Add(edit);
        return null;
    }

    /// <summary>Builds the final folder from the tree: folders created, files moved from staging.</summary>
    public static void Materialize(FileTree tree, string final, CancellationToken ct)
    {
        Directory.CreateDirectory(final);
        foreach (var entry in tree.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = SafePath.Resolve(final, entry.Path) ?? throw new InvalidOperationException($"{entry.Path} leaves the project.");
            if (entry.IsFolder)
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(entry.Source!, path);
        }
    }
}

/// <summary>
/// Passes each report straight on, on the reporting thread. <see cref="Progress{T}"/> would queue
/// every line separately to the thread pool when there's no UI context (inside Task.Run), and the
/// lines could then arrive out of order; the caller's own progress does the marshalling.
/// </summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>
/// A generation waiting in staging (a project, or an item template's changes to a folder): the
/// preview shows <see cref="Tree"/>, edits change it, and disposing throws it away.
/// </summary>
public interface IStagedResult : IDisposable
{
    /// <summary>The result, relative to <see cref="TargetPath"/>.</summary>
    FileTree Tree { get; }

    /// <summary>Where it goes: the project folder, or the folder an item template is applied to.</summary>
    string TargetPath { get; }

    /// <summary>The preview's edits so far, as concrete steps.</summary>
    IReadOnlyList<TemplateStep> Edits { get; }

    /// <summary>A preview edit (move / rename / delete / new folder, concrete paths). Returns why not, or null.</summary>
    string? ApplyEdit(TemplateStep edit);
}
