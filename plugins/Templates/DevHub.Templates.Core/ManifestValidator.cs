using System.Text.RegularExpressions;
using DevHub.Sdk.Core.Localization;

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
            Add("id", L.T("Use lowercase letters, digits and single dashes, e.g. \"unity-game\"."));
        if (string.IsNullOrWhiteSpace(manifest.Name))
            Add("name", L.T("Give the template a name."));
        if (string.IsNullOrWhiteSpace(manifest.Language))
            Add("language", L.T("Give a language, or \"Any\"."));

        for (var i = 0; i < manifest.Categories.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(manifest.Categories[i]))
                Add($"categories[{i}]", L.T("A category can't be empty."));
        }
        foreach (var duplicate in Duplicates(manifest.Categories))
            Add("categories", L.T($"\"{duplicate}\" is listed twice."));

        ValidateVariables(manifest, Add);
        ValidateKind(manifest, Add);
        ValidateSteps(manifest, Add);
        ValidatePostCreate(manifest, Add);

        for (var i = 0; i < manifest.BinaryExtensions.Count; i++)
        {
            if (manifest.BinaryExtensions[i] is not ['.', _, ..])
                Add($"binaryExtensions[{i}]", L.T("Write an extension with its dot, e.g. \".png\"."));
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
                add(at + ".name", L.T("Start with a letter; letters and digits only (e.g. \"ProjectName\")."));
            else if (BuiltInVariables.Contains(variable.Name))
                add(at + ".name", L.T($"\"{variable.Name}\" is built in; pick another name."));

            switch (variable.Type)
            {
                case VariableType.Choice when variable.Choices.Count == 0:
                    add(at + ".choices", L.T("A choice needs at least one option."));
                    break;
                case VariableType.Choice when variable.Default is not null && !variable.Choices.Contains(variable.DefaultText):
                    add(at + ".default", L.T($"\"{variable.DefaultText}\" isn't one of the choices."));
                    break;
                case VariableType.Bool when variable.Default is not null && variable.DefaultText is not ("true" or "false"):
                    add(at + ".default", L.T("A bool's default is true or false."));
                    break;
            }

            if (variable.Pattern is { } pattern)
            {
                if (variable.Type != VariableType.String)
                    add(at + ".pattern", L.T("Only string variables can have a pattern."));
                else if (!IsValidRegex(pattern))
                    add(at + ".pattern", L.T("This isn't a valid regular expression."));
                else if (variable.Default is not null && !Regex.IsMatch(variable.DefaultText, pattern))
                    add(at + ".default", L.T("The default doesn't match the pattern."));
            }
        }
        foreach (var duplicate in Duplicates(manifest.Variables.Select(v => v.Name)))
            add("variables", L.T($"\"{duplicate}\" is declared twice."));
    }

    private static void ValidateKind(TemplateManifest manifest, Action<string, string> add)
    {
        if (manifest.Kind == TemplateKind.Item)
        {
            if (manifest.RootFolder is not null)
                add("rootFolder", L.T("Item templates go into an existing folder; they have no root folder."));
            if (manifest.BaseCommand is not null)
                add("baseCommand", L.T("Only project templates can have a base command."));
            return;
        }

        if (manifest.RootFolder is null && !manifest.Variables.Any(v => v.Name == "Name"))
            add("rootFolder", L.T("The root folder defaults to {{Name}}: declare a Name variable or set rootFolder."));
        else if (manifest.RootFolder is { } root && (string.IsNullOrWhiteSpace(root) || root.Contains('/') || root.Contains('\\')))
            add("rootFolder", L.T("One folder name (placeholders allowed), no slashes."));

        if (manifest.BaseCommand is { } command && string.IsNullOrWhiteSpace(command.Command))
            add("baseCommand.command", L.T("Name the program to run, e.g. \"dotnet\"."));
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
                        add(at + ".to", L.T("Give the new name."));
                    else if (step.To.Contains('/') || step.To.Contains('\\') || step.To is "." or "..")
                        add(at + ".to", L.T("Rename takes a name, not a path (use move to change folders)."));
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
                add(at + ".name", L.T("Give the command a name (shown before it runs)."));
            if (string.IsNullOrWhiteSpace(command.Command))
                add(at + ".command", L.T("Name the program to run, e.g. \"git\"."));
            if (command.WorkingDirectory != ".")
                CheckPath(command.WorkingDirectory, at + ".workingDirectory", add);
            if (command.When is { } when && !manifest.Variables.Any(v => v.Name == when && v.Type == VariableType.Bool))
                add(at + ".when", L.T($"\"{when}\" isn't a bool variable of this template."));
        }
    }

    /// <summary>A path inside the root: relative, "/" separators, no "..", not empty.</summary>
    private static void CheckPath(string? path, string field, Action<string, string> add)
    {
        if (string.IsNullOrWhiteSpace(path))
            add(field, L.T("Give a path."));
        else if (path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':'))
            add(field, L.T("Use a path relative to the project root, e.g. \"src/App\"."));
        else if (path.Contains('\\'))
            add(field, L.T("Use / between folders."));
        else if (path.Split('/').Contains(".."))
            add(field, L.T("\"..\" isn't allowed: steps stay inside the project."));
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
