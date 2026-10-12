using System.Text.RegularExpressions;

namespace DevHub.Templates.Core.Rendering;

/// <summary>What's wrong with the value of one variable (shown under its field in the form).</summary>
public sealed record ValueIssue(string Variable, string Message);

/// <summary>The checked values, or what's wrong with them.</summary>
/// <param name="Values">Every declared variable → its value (defaults filled in; bools "true" / "false").</param>
public sealed record VariableValuesResult(IReadOnlyDictionary<string, string> Values, IReadOnlyList<ValueIssue> Issues)
{
    public bool Succeeded => Issues.Count == 0;
}

/// <summary>
/// Turns what the user typed into the values a generation renders with: every declared variable
/// gets a value (what was entered, else its default, else "" / "false"), checked against its rules
/// (required, pattern, one of the choices, a bool). Problems come back per variable, for the form.
/// </summary>
public static class VariableValues
{
    /// <param name="entered">Variable name → what the user entered (missing = not entered).</param>
    public static VariableValuesResult Check(TemplateManifest manifest, IReadOnlyDictionary<string, string> entered)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var issues = new List<ValueIssue>();

        foreach (var variable in manifest.Variables)
        {
            var value = entered.TryGetValue(variable.Name, out var typed) ? typed : variable.DefaultText;
            var label = variable.Label ?? variable.Name;

            switch (variable.Type)
            {
                case VariableType.Bool:
                    if (value.Length == 0)
                        value = "false";
                    else if (bool.TryParse(value, out var flag))
                        value = flag ? "true" : "false";
                    else
                        issues.Add(new ValueIssue(variable.Name, $"{label} must be true or false."));
                    break;

                case VariableType.Choice:
                    if (value.Length == 0 && !variable.Required)
                        break;
                    if (!variable.Choices.Contains(value))
                        issues.Add(new ValueIssue(variable.Name, $"Pick one of: {string.Join(", ", variable.Choices)}."));
                    break;

                default:
                    if (variable.Required && string.IsNullOrWhiteSpace(value))
                        issues.Add(new ValueIssue(variable.Name, $"{label} is required."));
                    else if (value.Length > 0 && variable.Pattern is { } pattern && !Matches(pattern, value))
                        issues.Add(new ValueIssue(variable.Name, $"{label} doesn't have the expected form ({pattern})."));
                    break;
            }
            values[variable.Name] = value;
        }
        return new VariableValuesResult(values, issues);
    }

    private static bool Matches(string pattern, string value)
    {
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false; // an invalid pattern is the validator's to report; here it just doesn't match
        }
    }
}
