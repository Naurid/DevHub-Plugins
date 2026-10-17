using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Git.Core;

/// <summary>Issue keys in branch names and commit messages ("feature/DEV-12-login", "DEV-12: fix the form").</summary>
public static partial class IssueKeys
{
    // A key is a project key and a number, not glued to other letters or digits ("xDEV-12" and "DEV-12a" aren't keys).
    [GeneratedRegex(@"(?<![A-Za-z0-9])([A-Za-z][A-Za-z0-9]{0,9})-(\d{1,7})(?![A-Za-z0-9])")]
    private static partial Regex KeyPattern();

    /// <summary>
    /// The keys of <paramref name="projectKey"/> in <paramref name="text"/>, in order, without repeats, upper case.
    /// Branch names are often lower case ("feature/dev-12-login"), so case doesn't matter.
    /// </summary>
    public static IReadOnlyList<string> Find(string? text, string projectKey)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var keys = new List<string>();
        foreach (Match match in KeyPattern().Matches(text))
        {
            if (!string.Equals(match.Groups[1].Value, projectKey, StringComparison.OrdinalIgnoreCase))
                continue;
            var key = projectKey.ToUpperInvariant() + "-" + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (!keys.Contains(key))
                keys.Add(key);
        }
        return keys;
    }

    /// <summary>The issues of <paramref name="board"/> mentioned in <paramref name="text"/> (old keys count too).</summary>
    public static IReadOnlyList<Issue> Issues(string? text, BoardSnapshot board)
    {
        var keys = Find(text, board.Project.Key).ToList();
        foreach (var issue in board.Issues.Values)
            foreach (var old in issue.OldKeys)
                if (IssueKey.TryParse(old, out var oldProject, out _))
                    keys.AddRange(Find(text, oldProject).Where(k => string.Equals(k, old, StringComparison.OrdinalIgnoreCase)));
        return keys.Select(board.IssueByKey).OfType<Issue>().DistinctBy(i => i.Id).ToList();
    }
}

/// <summary>Branch names for issues: <c>feature/DEV-12-login-form</c>, <c>bugfix/DEV-7-crash-on-start</c>.</summary>
public static class BranchNames
{
    public const int MaxSlugLength = 40;

    public static string For(Issue issue, BoardProject project)
    {
        var level = project.IssueType(issue.Type);
        var prefix = level?.Icon == "bug" || issue.Type == "bug" ? "bugfix" : "feature";
        var slug = Slug(issue.Summary);
        return slug.Length == 0 ? $"{prefix}/{issue.Key}" : $"{prefix}/{issue.Key}-{slug}";
    }

    /// <summary>"Login form: e-mail & password!" → "login-form-e-mail-password" (ASCII letters, digits, hyphens).</summary>
    public static string Slug(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var slug = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;   // é → e
            if (char.IsAsciiLetterOrDigit(c))
                slug.Append(char.ToLowerInvariant(c));
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }
        var result = slug.ToString().Trim('-');
        if (result.Length > MaxSlugLength)
            result = result[..MaxSlugLength].TrimEnd('-');
        // Cut at a word boundary when that keeps most of it.
        return result;
    }
}

/// <summary>What a smart commit asks for.</summary>
public abstract record SmartCommand(Issue Issue)
{
    /// <summary>Move to a status ("#done", "#in-progress", "#review").</summary>
    public sealed record Transition(Issue Issue, Status Status) : SmartCommand(Issue);

    /// <summary>Add a comment ("#comment text").</summary>
    public sealed record Comment(Issue Issue, string Text) : SmartCommand(Issue);

    /// <summary>Log work ("#time 2h 30m").</summary>
    public sealed record Time(Issue Issue, int Minutes) : SmartCommand(Issue);

    /// <summary>For the confirmation: "DEV-12: move to Done".</summary>
    public string Describe(ProjectSettings settings) => this switch
    {
        Transition t => L.T($"{Issue.Key}: move to {t.Status.Name}"),
        Comment c => L.T($"{Issue.Key}: comment \"{(c.Text.Length > 60 ? c.Text[..60] + "…" : c.Text)}\""),
        Time w => L.T($"{Issue.Key}: log {WorkDuration.Format(w.Minutes, settings)}"),
        _ => Issue.Key,
    };

    /// <summary>The board operation that does it.</summary>
    public IBoardOperation ToOperation(DateTimeOffset now) => this switch
    {
        Transition t => new TransitionIssue(Issue.Id, t.Status.Id),
        Comment c => new AddComment(Issue.Id, c.Text),
        Time w => new LogWork(Issue.Id, w.Minutes, now),
        _ => throw new InvalidOperationException(),
    };
}

/// <summary>
/// Smart commits, Jira style: a commit message naming an issue and commands for it —
/// <c>DEV-12 #done #comment fixed the layout #time 2h</c>. DevHub shows what they'd do and applies them only when you
/// say so (docs/BOARD_PLAN.md: never silent). A status command names a status (spaces as hyphens: #in-progress) or
/// a category ("#done" picks the issue's first Done status its workflow allows).
/// </summary>
public static partial class SmartCommits
{
    [GeneratedRegex(@"#([A-Za-z][A-Za-z0-9_-]*)")]
    private static partial Regex CommandPattern();

    public static IReadOnlyList<SmartCommand> Parse(string? message, BoardSnapshot board)
    {
        if (string.IsNullOrWhiteSpace(message))
            return [];
        var issues = IssueKeys.Issues(message, board);
        if (issues.Count == 0)
            return [];
        var commands = new List<SmartCommand>();
        var matches = CommandPattern().Matches(message);
        for (var m = 0; m < matches.Count; m++)
        {
            var match = matches[m];
            var word = match.Groups[1].Value;
            // The command's text runs to the next command (or the end of the line).
            var end = m + 1 < matches.Count ? matches[m + 1].Index : message.Length;
            var lineEnd = message.IndexOf('\n', match.Index);
            if (lineEnd >= 0 && lineEnd < end)
                end = lineEnd;
            var text = message[(match.Index + match.Length)..end].Trim();

            foreach (var issue in issues)
            {
                switch (word.ToLowerInvariant())
                {
                    case "comment":
                        if (text.Length > 0)
                            commands.Add(new SmartCommand.Comment(issue, text));
                        break;
                    case "time":
                        if (WorkDuration.TryParse(text.Split("#")[0].Trim(), board.Project.Settings, out var minutes))
                            commands.Add(new SmartCommand.Time(issue, minutes));
                        break;
                    default:
                        if (StatusFor(word, issue, board) is { } status)
                            commands.Add(new SmartCommand.Transition(issue, status));
                        break;
                }
            }
        }
        return commands;
    }

    private static Status? StatusFor(string word, Issue issue, BoardSnapshot board)
    {
        var workflow = board.WorkflowFor(issue.Type);
        if (workflow is null)
            return null;
        var allowed = workflow.Statuses.Where(s => s == issue.Status || workflow.Allows(issue.Status, s))
            .Select(board.Project.Status).OfType<Status>().ToList();
        var normalized = Normalize(word);
        var byName = allowed.FirstOrDefault(s => Normalize(s.Name) == normalized);
        if (byName is not null)
            return byName.Id == issue.Status ? null : byName;
        var category = normalized switch
        {
            "done" or "close" or "closed" or "resolve" or "resolved" or "fixed" => StatusCategory.Done,
            "start" or "started" or "inprogress" or "wip" => StatusCategory.InProgress,
            "reopen" or "todo" => StatusCategory.ToDo,
            _ => (StatusCategory?)null,
        };
        return category is { } c ? allowed.FirstOrDefault(s => s.Category == c && s.Id != issue.Status) : null;
    }

    private static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
