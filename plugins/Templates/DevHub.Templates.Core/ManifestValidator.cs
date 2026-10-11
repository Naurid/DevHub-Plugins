using System.Text.RegularExpressions;

namespace DevHub.Templates.Core;

/// <summary>One problem in a manifest: where (e.g. "variables[1].name") and what, in plain words.</summary>
public sealed record ManifestIssue(string Field, string Message)
{
    public override string ToString() => $"{Field}: {Message}";
}

/// <summary>
/// Checks a manifest against the spec (docs/TEMPLATE_FORMAT.md §3, §6) before it's saved or used.
/// Returns every problem found (empty = valid), never throws for a bad manifest: the template
/// editor shows the list, the browser marks the template as broken.
/// </summary>
public static partial class ManifestValidator
{
    /// <summary>Built-in variables (§5): can't be declared by a template.</summary>
    public static readonly IReadOnlySet<string> BuiltInVariables = new HashSet<string>(
        ["Year", "Date", "Author", .. Enumerable.Range(1, 9).Select(i => "Guid" + i)], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex VariableNamePattern();

    public static IReadOnlyList<ManifestIssue> Validate(TemplateManifest manifest)
    {
        var issues = new List<ManifestIssue>();
        void Add(string field, string message) => issues.Add(new ManifestIssue(field, message));

        if (!IdPattern().IsMatch(manifest.Id))
            Add("id", "Use lowercase letters, digits and single dashes, e.g. \"unity-game\".");
        if (string.IsNullOrWhiteSpace(manifest.Name))
            Add("name", "Give the template a name.");
        if (string.IsNullOrWhiteSpace(manifest.Language))
            Add("language", "Give a language, or \"Any\".");

        for (var i = 0; i < manifest.Categories.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(manifest.Categories[i]))
                Add($"categories[{i}]", "A category can't be empty.");
        }
        foreach (var duplicate in Duplicates(manifest.Categories))
            Add("categories", $"\"{duplicate}\" is listed twice.");

        ValidateVariables(manifest, Add);
        ValidateKind(manifest, Add);
        ValidateSteps(manifest, Add);
        ValidatePostCreate(manifest, Add);

        for (var i = 0; i < manifest.BinaryExtensions.Count; i++)
        {
            if (manifest.BinaryExtensions[i] is not ['.', _, ..])
                Add($"binaryExtensions[{i}]", "Write an extension with its dot, e.g. \".png\".");
        }
        return issues;
    }

    private static void ValidateVariables(TemplateManifest manifest, Action<string, string> add)
    {
        for (var i = 0; i < manifest.Variables.Count; i++)
        {
            var variable = manifest.Variables[i];
            var at = $"variables[{i}]";
            if (!VariableNamePattern().IsMatch(variable.Name))
                add(at + ".name", "Start with a letter; letters and digits only (e.g. \"ProjectName\").");
            else if (BuiltInVariables.Contains(variable.Name))
                add(at + ".name", $"\"{variable.Name}\" is built in; pick another name.");

            switch (variable.Type)
            {
                case VariableType.Choice when variable.Choices.Count == 0:
                    add(at + ".choices", "A choice needs at least one option.");
                    break;
                case VariableType.Choice when variable.Default is not null && !variable.Choices.Contains(variable.DefaultText):
                    add(at + ".default", $"\"{variable.DefaultText}\" isn't one of the choices.");
                    break;
                case VariableType.Bool when variable.Default is not null && variable.DefaultText is not ("true" or "false"):
                    add(at + ".default", "A bool's default is true or false.");
                    break;
            }

            if (variable.Pattern is { } pattern)
            {
                if (variable.Type != VariableType.String)
                    add(at + ".pattern", "Only string variables can have a pattern.");
                else if (!IsValidRegex(pattern))
                    add(at + ".pattern", "This isn't a valid regular expression.");
                else if (variable.Default is not null && !Regex.IsMatch(variable.DefaultText, pattern))
                    add(at + ".default", "The default doesn't match the pattern.");
            }
        }
        foreach (var duplicate in Duplicates(manifest.Variables.Select(v => v.Name)))
            add("variables", $"\"{duplicate}\" is declared twice.");
    }

    private static void ValidateKind(TemplateManifest manifest, Action<string, string> add)
    {
        if (manifest.Kind == TemplateKind.Item)
        {
            if (manifest.RootFolder is not null)
                add("rootFolder", "Item templates go into an existing folder; they have no root folder.");
            if (manifest.BaseCommand is not null)
                add("baseCommand", "Only project templates can have a base command.");
            return;
        }

        if (manifest.RootFolder is null && !manifest.Variables.Any(v => v.Name == "Name"))
            add("rootFolder", "The root folder defaults to {{Name}}: declare a Name variable or set rootFolder.");
        else if (manifest.RootFolder is { } root && (string.IsNullOrWhiteSpace(root) || root.Contains('/') || root.Contains('\\')))
            add("rootFolder", "One folder name (placeholders allowed), no slashes.");

        if (manifest.BaseCommand is { } command && string.IsNullOrWhiteSpace(command.Command))
            add("baseCommand.command", "Name the program to run, e.g. \"dotnet\".");
    }

    private static void ValidateSteps(TemplateManifest manifest, Action<string, string> add)
    {
        for (var i = 0; i < manifest.Steps.Count; i++)
        {
            var step = manifest.Steps[i];
            var at = $"steps[{i}]";
            switch (step.Op)
            {
                case StepOp.CreateFolder or StepOp.Delete:
                    CheckPath(step.Path, at + ".path", add);
                    break;
                case StepOp.Move:
                    CheckPath(step.From, at + ".from", add);
                    CheckPath(step.To, at + ".to", add);
                    break;
                case StepOp.Rename:
                    CheckPath(step.From, at + ".from", add);
                    if (string.IsNullOrWhiteSpace(step.To))
                        add(at + ".to", "Give the new name.");
                    else if (step.To.Contains('/') || step.To.Contains('\\') || step.To is "." or "..")
                        add(at + ".to", "Rename takes a name, not a path (use move to change folders).");
                    break;
            }
        }
    }

    private static void ValidatePostCreate(TemplateManifest manifest, Action<string, string> add)
    {
        for (var i = 0; i < manifest.PostCreate.Count; i++)
        {
            var command = manifest.PostCreate[i];
            var at = $"postCreate[{i}]";
            if (string.IsNullOrWhiteSpace(command.Name))
                add(at + ".name", "Give the command a name (shown before it runs).");
            if (string.IsNullOrWhiteSpace(command.Command))
                add(at + ".command", "Name the program to run, e.g. \"git\".");
            if (command.WorkingDirectory != ".")
                CheckPath(command.WorkingDirectory, at + ".workingDirectory", add);
            if (command.When is { } when && !manifest.Variables.Any(v => v.Name == when && v.Type == VariableType.Bool))
                add(at + ".when", $"\"{when}\" isn't a bool variable of this template.");
        }
    }

    /// <summary>A path inside the root: relative, "/" separators, no "..", not empty.</summary>
    private static void CheckPath(string? path, string field, Action<string, string> add)
    {
        if (string.IsNullOrWhiteSpace(path))
            add(field, "Give a path.");
        else if (path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':'))
            add(field, "Use a path relative to the project root, e.g. \"src/App\".");
        else if (path.Contains('\\'))
            add(field, "Use / between folders.");
        else if (path.Split('/').Contains(".."))
            add(field, "\"..\" isn't allowed: steps stay inside the project.");
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IEnumerable<string> Duplicates(IEnumerable<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
}
