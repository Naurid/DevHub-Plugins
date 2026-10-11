using System.Text.RegularExpressions;
using DevHub.Templates.Core.Commands;
using DevHub.Templates.Core.Rendering;

namespace DevHub.Templates.Core.Generation;

/// <summary>Runs the post-create commands (§4 step 6) in the placed project / the item's target folder.</summary>
public static class PostCreate
{
    /// <summary>
    /// Runs the commands in order (<see cref="TemplateRendering.PostCreate"/>, so <c>when</c> is already
    /// applied). A failing command stops the rest unless it has <c>continueOnError</c>.
    /// </summary>
    /// <returns>A result per command that ran.</returns>
    public static async Task<IReadOnlyList<CommandResult>> RunAsync(CommandRunner runner, IReadOnlyList<RenderedCommand> commands,
        string root, IProgress<CommandOutputLine>? progress = null, CancellationToken ct = default)
    {
        var results = new List<CommandResult>();
        foreach (var command in commands)
        {
            CommandResult result;
            if (SafePath.Resolve(root, command.WorkingDirectory) is not { } folder)
                result = new CommandResult(command, null, $"{command.Name}: its folder \"{command.WorkingDirectory}\" is outside the project.", []);
            else
                result = await runner.RunAsync(command, folder, progress, ct);

            results.Add(result);
            if (!result.Succeeded && !command.ContinueOnError)
                break;
        }
        return results;
    }
}

/// <summary>
/// "Save to template" (§4 step 4): the preview's edits (concrete paths like "src/Timber/App.cs")
/// turned into template steps, with this generation's variable values put back as placeholders
/// ("src/{{Name}}/App.cs"), so they work for the next project too.
/// </summary>
public static class PreviewEdits
{
    /// <param name="values">The values used for this generation (only string variables are put back; "true" is too common).</param>
    public static IReadOnlyList<TemplateStep> ToTemplateSteps(IReadOnlyList<TemplateStep> edits, TemplateManifest manifest,
        IReadOnlyDictionary<string, string> values)
    {
        // Longest value first, one pass, whole value only: "MyGame" and "MyGameTests" both stay right.
        var replacements = manifest.Variables
            .Where(v => v.Type == VariableType.String && values.TryGetValue(v.Name, out var value) && value.Length >= 2)
            .Select(v => (Value: values[v.Name], Token: "{{" + v.Name + "}}"))
            .GroupBy(r => r.Value, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(r => r.Value.Length)
            .ToList();
        if (replacements.Count == 0)
            return edits;

        var pattern = new Regex(string.Join('|', replacements.Select(r => Regex.Escape(r.Value))));
        var tokens = replacements.ToDictionary(r => r.Value, r => r.Token, StringComparer.Ordinal);
        string? Put(string? path) => path is null ? null : pattern.Replace(path, m => tokens[m.Value]);

        return edits.Select(e => e with { Path = Put(e.Path), From = Put(e.From), To = Put(e.To) }).ToList();
    }
}
