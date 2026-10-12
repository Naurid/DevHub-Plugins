using DevHub.Services;
using DevHub.Templates.Core;
using DevHub.Templates.Core.Commands;
using DevHub.Templates.Core.Generation;
using DevHub.Templates.Core.Rendering;

namespace DevHub.ViewModels.Templates;

/// <summary>What New Project and Add Files do the same way: Save to Template, and running the post-create commands.</summary>
internal static class TemplateFlow
{
    /// <summary>
    /// Save to Template: the preview's edits become steps of the template (of a copy in the personal
    /// library when the template is read-only).
    /// </summary>
    /// <param name="what">"project" / "files", for the message ("The next project from it is arranged this way").</param>
    /// <returns>The template as saved (the generation continues with it), or null, and what happened.</returns>
    public static async Task<(TemplateEntry? Saved, SaveOutcome Outcome)> SaveEditsAsync(TemplatesService templates,
        TemplateEntry template, IReadOnlyDictionary<string, string> values, IReadOnlyList<TemplateStep> edits, string what)
    {
        var steps = PreviewEdits.ToTemplateSteps(edits, template.Manifest!, values);
        try
        {
            var target = template;
            if (template.Library.IsReadOnly)
            {
                var copy = await TemplateStore.DuplicateAsync(template, templates.PersonalLibrary);
                if (copy.Entry is not { } copied)
                    return (null, new SaveOutcome(false, "Couldn't copy the template: " + string.Join(" ", copy.Errors)));
                target = copied;
            }

            var saved = await TemplateStore.SaveAsync(target, target.Manifest! with { Steps = [.. target.Manifest!.Steps, .. steps] });
            if (saved.Entry is not { } entry)
                return (null, new SaveOutcome(false, "Couldn't save the template: " + string.Join(" ", saved.Errors)));

            templates.NotifyChanged(); // the Templates tab and open editors see it too
            var count = steps.Count == 1 ? "1 step" : $"{steps.Count} steps";
            return (entry, new SaveOutcome(true, $"Saved {count} to {entry.Reference}. The next {what} from it is arranged this way."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, new SaveOutcome(false, "Couldn't save the template: " + ex.Message));
        }
    }

    /// <summary>
    /// Runs the post-create commands one at a time (so the log can say which command the output is
    /// from) on a run page with Cancel, also in the status bar. Stops at a failure unless that command
    /// continues on errors.
    /// </summary>
    /// <param name="show">Shows the run page.</param>
    public static async Task<(IReadOnlyList<CommandResult> Results, bool Cancelled)> RunPostCreateAsync(TemplatesService templates,
        IStatusService status, IReadOnlyList<RenderedCommand> commands, string folder, OutputLogViewModel log, Action<RunPageViewModel> show)
    {
        var name = Path.GetFileName(folder);
        var cts = new CancellationTokenSource();
        var run = new RunPageViewModel($"Setting up {name}", log, cts.Cancel, () => { });
        show(run);
        var task = status.StartTask($"Setting up {name}…", cts.Cancel);

        var results = new List<CommandResult>();
        var cancelled = false;
        try
        {
            foreach (var command in commands)
            {
                run.Step = command.Name;
                task.Report(command.Name, null);
                log.Add("▶ " + command.Name + ": " + command.DisplayText, isHeader: true);
                var output = new Progress<CommandOutputLine>(log.Add);
                var ct = cts.Token;
                var result = (await Task.Run(() => PostCreate.RunAsync(templates.Runner, [command], folder, output, ct), ct)).Single();
                results.Add(result);
                if (result.Error is { } error)
                    log.Add(error, isError: true);
                if (!result.Succeeded && !command.ContinueOnError)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            log.Add("Couldn't run the commands: " + ex.Message, isError: true);
            cancelled = true;
        }
        finally
        {
            run.IsRunning = false; // Cancel off before its token source goes
            task.Dispose();
            cts.Dispose();
        }
        return (results, cancelled);
    }
}
