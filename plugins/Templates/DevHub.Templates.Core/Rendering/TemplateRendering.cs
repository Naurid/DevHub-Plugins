namespace DevHub.Templates.Core.Rendering;

/// <summary>A command with placeholders filled in, ready to show in the confirmation and to run.</summary>
/// <param name="Name">What it's called in the confirmation ("Initialize git", or "Base command").</param>
/// <param name="WorkingDirectory">Relative to the project root (item: the target folder).</param>
public sealed record RenderedCommand(string Name, string Command, IReadOnlyList<string> Args, string WorkingDirectory)
{
    /// <summary>Post-create: keep going with the next command when this one fails.</summary>
    public bool ContinueOnError { get; init; }

    /// <summary>How it reads in the confirmation: <c>dotnet new console -n "My App"</c> (display only, never run as a string).</summary>
    public string DisplayText => string.Join(' ', new[] { Command }.Concat(Args.Select(Quote)));

    private static string Quote(string arg) =>
        arg.Length == 0 ? "\"\"" : arg.Any(c => char.IsWhiteSpace(c) || c == '"') ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;
}

/// <summary>A rendered step: its paths filled in and checked, or why it can't run.</summary>
public sealed record RenderedStep(StepOp Op, string? Path, string? From, string? To, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Renders the parts of a manifest that aren't file contents (§5 rule 1): the root folder, the
/// commands (base and post-create, with their <c>when</c> conditions applied) and the step paths.
/// </summary>
public static class TemplateRendering
{
    /// <summary>The project's root folder name (§3 <c>rootFolder</c>), checked as one name.</summary>
    public static RenderedPath RootFolder(TemplateManifest manifest, RenderContext context) =>
        PathRenderer.RenderSegment(manifest.EffectiveRootFolder, context);

    /// <summary>The base command, or null when the template has none.</summary>
    public static RenderedCommand? BaseCommand(TemplateManifest manifest, RenderContext context) =>
        manifest.BaseCommand is { } command
            ? new RenderedCommand("Base command", PlaceholderRenderer.Render(command.Command, context),
                command.Args.Select(a => PlaceholderRenderer.Render(a, context)).ToList(), ".")
            : null;

    /// <summary>The post-create commands that will run: those whose <c>when</c> variable is "true" (or that have none).</summary>
    public static IReadOnlyList<RenderedCommand> PostCreate(TemplateManifest manifest, RenderContext context) =>
        manifest.PostCreate
            .Where(c => c.When is null || context[c.When] == "true")
            .Select(c => new RenderedCommand(
                PlaceholderRenderer.Render(c.Name, context),
                PlaceholderRenderer.Render(c.Command, context),
                c.Args.Select(a => PlaceholderRenderer.Render(a, context)).ToList(),
                c.WorkingDirectory == "." ? "." : PlaceholderRenderer.Render(c.WorkingDirectory, context))
            {
                ContinueOnError = c.ContinueOnError,
            })
            .ToList();

    /// <summary>Every command this generation will run, in order: what the confirmation lists (§4).</summary>
    /// <param name="raw">A raw project (§4): only the base command runs.</param>
    public static IReadOnlyList<RenderedCommand> AllCommands(TemplateManifest manifest, RenderContext context, bool raw = false) =>
        [.. BaseCommand(manifest, context) is { } b ? [b] : Array.Empty<RenderedCommand>(), .. raw ? [] : PostCreate(manifest, context)];

    /// <summary>The steps with their paths rendered (globs allowed in from / path) and checked.</summary>
    public static IReadOnlyList<RenderedStep> Steps(TemplateManifest manifest, RenderContext context) =>
        manifest.Steps.Select(step =>
        {
            RenderedPath? path = step.Path is null ? null : PathRenderer.RenderPath(step.Path, context, allowGlobs: true);
            RenderedPath? from = step.From is null ? null : PathRenderer.RenderPath(step.From, context, allowGlobs: true);
            RenderedPath? to = step.To is null ? null
                : step.Op == StepOp.Rename ? PathRenderer.RenderSegment(step.To, context)
                : PathRenderer.RenderPath(step.To, context);
            var error = new[] { path, from, to }.FirstOrDefault(p => p is { Succeeded: false })?.Error;
            return new RenderedStep(step.Op, path?.Path, from?.Path, to?.Path, error);
        }).ToList();
}
