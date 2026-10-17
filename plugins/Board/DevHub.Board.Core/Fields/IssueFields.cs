using System.Globalization;
using DevHub.Board.Core.Model;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Core.Fields;

/// <summary>A field's value: text, a number, a date, a list of ids / labels, or minutes. Null inside = empty.</summary>
public abstract record FieldValue
{
    public sealed record Text(string? Value) : FieldValue;
    public sealed record Number(double? Value) : FieldValue;
    public sealed record Date(DateOnly? Value) : FieldValue;
    public sealed record Items(IReadOnlyList<string> Values) : FieldValue
    {
        public bool Equals(Items? other) => other is not null && Values.SequenceEqual(other.Values);
        public override int GetHashCode() => Values.Count;
    }
    public sealed record Minutes(int? Value) : FieldValue;

    /// <summary>Raw text for history lines (ids, invariant numbers, yyyy-MM-dd, minutes, comma-separated lists); null = empty.</summary>
    public string? Raw => this switch
    {
        Text t => string.IsNullOrEmpty(t.Value) ? null : t.Value,
        Number n => n.Value?.ToString("R", CultureInfo.InvariantCulture),
        Date d => d.Value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Items i => i.Values.Count == 0 ? null : string.Join(",", i.Values),
        Minutes m => m.Value?.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };
}

/// <summary>
/// Every issue field by name: read, set and check them in one place. Editing in the issue view, bulk edit,
/// imports, smart commits and the M10 command tools all change fields through here, so the rules are the same
/// everywhere. Status and resolution change only through a transition (<c>TransitionIssue</c>).
/// </summary>
public static class IssueFields
{
    public const string Summary = "summary";
    public const string Description = "description";
    public const string Type = "type";
    public const string Status = "status";
    public const string Resolution = "resolution";
    public const string Priority = "priority";
    public const string Parent = "parent";
    public const string Assignee = "assignee";
    public const string Reporter = "reporter";
    public const string Labels = "labels";
    public const string Components = "components";
    public const string FixVersions = "fixVersions";
    public const string StoryPoints = "storyPoints";
    public const string OriginalEstimate = "originalEstimate";
    public const string RemainingEstimate = "remainingEstimate";
    public const string Start = "start";
    public const string Due = "due";
    public const string Sprint = "sprint";
    public const string Rank = "rank";
    public const string CustomPrefix = "custom.";

    public const int MaxSummaryLength = 255;

    /// <summary>The fields <see cref="Set"/> accepts (custom fields: <c>custom.&lt;id&gt;</c>).</summary>
    public static IReadOnlyList<string> Editable { get; } =
    [
        Summary, Description, Type, Priority, Parent, Assignee, Reporter, Labels, Components, FixVersions,
        StoryPoints, OriginalEstimate, RemainingEstimate, Start, Due, Sprint,
    ];

    /// <summary>Fields compared for the change log (besides custom fields).</summary>
    internal static IReadOnlyList<string> Tracked { get; } = [.. Editable, Status, Resolution];

    public static string Custom(string fieldId) => CustomPrefix + fieldId;

    /// <summary>For people: "Story points", or a custom field's name.</summary>
    public static string DisplayName(string field, BoardProject project) => field switch
    {
        Summary => L.T("Summary"),
        Description => L.T("Description"),
        Type => L.T("Type"),
        Status => L.T("Status"),
        Resolution => L.T("Resolution"),
        Priority => L.T("Priority"),
        Parent => L.T("Parent"),
        Assignee => L.T("Assignee"),
        Reporter => L.T("Reporter"),
        Labels => L.T("Labels"),
        Components => L.T("Components"),
        FixVersions => L.T("Fix versions"),
        StoryPoints => L.T("Story points"),
        OriginalEstimate => L.T("Original estimate"),
        RemainingEstimate => L.T("Remaining estimate"),
        Start => L.T("Start date"),
        Due => L.T("Due date"),
        Sprint => L.T("Sprint"),
        Rank => L.T("Rank"),
        _ when field.StartsWith(CustomPrefix, StringComparison.Ordinal) =>
            project.CustomField(field[CustomPrefix.Length..])?.Name ?? field[CustomPrefix.Length..],
        _ => field,
    };

    public static FieldValue Get(Issue issue, string field) => field switch
    {
        Summary => new FieldValue.Text(issue.Summary),
        Description => new FieldValue.Text(issue.Description),
        Type => new FieldValue.Text(issue.Type),
        Status => new FieldValue.Text(issue.Status),
        Resolution => new FieldValue.Text(issue.Resolution),
        Priority => new FieldValue.Text(issue.Priority),
        Parent => new FieldValue.Text(issue.Parent),
        Assignee => new FieldValue.Text(issue.Assignee),
        Reporter => new FieldValue.Text(issue.Reporter),
        Labels => new FieldValue.Items(issue.Labels),
        Components => new FieldValue.Items(issue.Components),
        FixVersions => new FieldValue.Items(issue.FixVersions),
        StoryPoints => new FieldValue.Number(issue.StoryPoints),
        OriginalEstimate => new FieldValue.Minutes(issue.Time.OriginalMinutes),
        RemainingEstimate => new FieldValue.Minutes(issue.Time.RemainingMinutes),
        Start => new FieldValue.Date(issue.Start),
        Due => new FieldValue.Date(issue.Due),
        Sprint => new FieldValue.Text(issue.Sprint),
        Rank => new FieldValue.Text(issue.Rank),
        _ when field.StartsWith(CustomPrefix, StringComparison.Ordinal) =>
            new FieldValue.Text(issue.Custom.GetValueOrDefault(field[CustomPrefix.Length..])),
        _ => throw new BoardRuleException(L.T($"There's no field \"{field}\".")),
    };

    /// <summary>The issue with <paramref name="field"/> set to <paramref name="value"/> (not checked: see <see cref="Validate"/>).</summary>
    public static Issue Set(Issue issue, string field, FieldValue value)
    {
        return field switch
        {
            Summary => issue with { Summary = Text()?.Trim() ?? "" },
            Description => issue with { Description = Text() ?? "" },
            Type => issue with { Type = Text() ?? issue.Type },
            Priority => issue with { Priority = Text() },
            Parent => issue with { Parent = Text() },
            Assignee => issue with { Assignee = Text() },
            Reporter => issue with { Reporter = Text() },
            Labels => issue with { Labels = NormalizeLabels(List()) },
            Components => issue with { Components = Distinct(List()) },
            FixVersions => issue with { FixVersions = Distinct(List()) },
            StoryPoints => issue with { StoryPoints = Number() },
            OriginalEstimate => issue with { Time = issue.Time with { OriginalMinutes = MinutesValue() } },
            RemainingEstimate => issue with { Time = issue.Time with { RemainingMinutes = MinutesValue() } },
            Start => issue with { Start = DateValue() },
            Due => issue with { Due = DateValue() },
            Sprint => issue with { Sprint = Text() },
            _ when field.StartsWith(CustomPrefix, StringComparison.Ordinal) => SetCustom(field[CustomPrefix.Length..], Text()),
            Status or Resolution => throw new BoardRuleException(L.T("Status and resolution change through a transition.")),
            _ => throw new BoardRuleException(L.T($"There's no field \"{field}\".")),
        };

        string? Text() => value switch
        {
            FieldValue.Text t => string.IsNullOrEmpty(t.Value) ? null : t.Value,
            FieldValue.Number n => n.Value?.ToString("R", CultureInfo.InvariantCulture),
            FieldValue.Date d => d.Value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FieldValue.Minutes m => m.Value?.ToString(CultureInfo.InvariantCulture),
            FieldValue.Items i => i.Values.Count == 0 ? null : string.Join(",", i.Values),
            _ => null,
        };
        IReadOnlyList<string> List() => value switch
        {
            FieldValue.Items i => i.Values,
            FieldValue.Text { Value: { Length: > 0 } t } => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => [],
        };
        double? Number() => value switch
        {
            FieldValue.Number n => n.Value,
            FieldValue.Text { Value: { Length: > 0 } t } => double.Parse(t, CultureInfo.InvariantCulture),
            _ => null,
        };
        int? MinutesValue() => value switch
        {
            FieldValue.Minutes m => m.Value,
            FieldValue.Number { Value: { } n } => (int)Math.Round(n),
            _ => null,
        };
        DateOnly? DateValue() => value switch
        {
            FieldValue.Date d => d.Value,
            FieldValue.Text { Value: { Length: > 0 } t } => DateOnly.ParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => null,
        };
        Issue SetCustom(string id, string? text)
        {
            var custom = new SortedDictionary<string, string>(issue.Custom, StringComparer.Ordinal);
            if (text is null)
                custom.Remove(id);
            else
                custom[id] = text;
            return issue with { Custom = custom };
        }
    }

    /// <summary>
    /// Checks that <paramref name="value"/> may go into <paramref name="field"/> of <paramref name="issue"/>.
    /// </summary>
    /// <exception cref="BoardRuleException">With a sentence saying why not.</exception>
    public static void Validate(BoardSnapshot board, Issue issue, string field, FieldValue value)
    {
        var project = board.Project;
        var text = value is FieldValue.Text t ? (string.IsNullOrEmpty(t.Value) ? null : t.Value) : value.Raw;

        switch (field)
        {
            case Summary:
                if (string.IsNullOrWhiteSpace(text))
                    throw new BoardRuleException(L.T("The summary can't be empty."));
                if (text.Trim().Length > MaxSummaryLength)
                    throw new BoardRuleException(L.T($"The summary is longer than {MaxSummaryLength} characters."));
                break;
            case Description:
                break;
            case Type:
                ValidateType(board, issue, text);
                break;
            case Priority:
                if (text is not null && project.Priority(text) is null)
                    throw new BoardRuleException(L.T($"There's no priority \"{text}\"."));
                break;
            case Parent:
                ValidateParent(board, issue, issue.Type, text);
                break;
            case Assignee or Reporter:
                if (text is not null && project.Person(text) is null)
                    throw new BoardRuleException(L.T($"There's nobody with the id \"{text}\" on this board."));
                break;
            case Labels:
                foreach (var label in Items(value))
                {
                    if (label.Any(char.IsWhiteSpace) || label.Contains(','))
                        throw new BoardRuleException(L.T($"A label can't contain spaces or commas (\"{label}\")."));
                }
                break;
            case Components:
                foreach (var id in Items(value).Where(id => project.Component(id) is null))
                    throw new BoardRuleException(L.T($"There's no component \"{id}\"."));
                break;
            case FixVersions:
                foreach (var id in Items(value).Where(id => !board.Versions.ContainsKey(id)))
                    throw new BoardRuleException(L.T($"There's no version \"{id}\"."));
                break;
            case StoryPoints:
                var points = value switch
                {
                    FieldValue.Number n => n.Value,
                    FieldValue.Text { Value: { Length: > 0 } s } when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
                    FieldValue.Text { Value: { Length: > 0 } } => throw new BoardRuleException(L.T("Story points must be a number.")),
                    _ => null,
                };
                if (points is < 0 or > 10000 || (points is { } pt && !double.IsFinite(pt)))
                    throw new BoardRuleException(L.T("Story points must be between 0 and 10000."));
                break;
            case OriginalEstimate or RemainingEstimate:
                if (value is FieldValue.Minutes { Value: < 0 } or FieldValue.Number { Value: < 0 })
                    throw new BoardRuleException(L.T("An estimate can't be negative."));
                if (value is not (FieldValue.Minutes or FieldValue.Number))
                    throw new BoardRuleException(L.T("An estimate is a number of minutes."));
                break;
            case Start or Due:
                if (value is FieldValue.Text { Value: { Length: > 0 } d }
                    && !DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw new BoardRuleException(L.T("A date looks like 2026-10-31."));
                var date = value switch
                {
                    FieldValue.Date { Value: { } dv } => dv,
                    FieldValue.Text { Value: { Length: > 0 } dt } => DateOnly.ParseExact(dt, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    _ => (DateOnly?)null,
                };
                if (date is { } chosen && (field == Start ? issue.Due is { } due && chosen > due : issue.Start is { } start && chosen < start))
                    throw new BoardRuleException(L.T("The start date can't be after the due date."));
                break;
            case Sprint:
                if (text is not null && !board.Sprints.ContainsKey(text))
                    throw new BoardRuleException(L.T($"There's no sprint \"{text}\"."));
                if (board.LevelOf(issue) == IssueTypeLevel.Subtask && text != board.Issue(issue.Parent)?.Sprint)
                    throw new BoardRuleException(L.T("A sub-task is in its parent's sprint."));
                break;
            case Status or Resolution:
                throw new BoardRuleException(L.T("Status and resolution change through a transition."));
            default:
                if (field.StartsWith(CustomPrefix, StringComparison.Ordinal))
                {
                    ValidateCustom(board, field[CustomPrefix.Length..], text);
                    break;
                }
                throw new BoardRuleException(L.T($"There's no field \"{field}\"."));
        }
    }

    /// <summary>Checks that an issue of type <paramref name="typeId"/> may have <paramref name="parentId"/> as its parent.</summary>
    public static void ValidateParent(BoardSnapshot board, Issue? issue, string typeId, string? parentId)
    {
        var level = board.Project.IssueType(typeId)?.Level ?? IssueTypeLevel.Standard;
        if (parentId is null)
        {
            if (level == IssueTypeLevel.Subtask)
                throw new BoardRuleException(L.T("A sub-task needs a parent issue."));
            return;
        }
        var parent = board.Issue(parentId) ?? throw new BoardRuleException(L.T("The parent issue doesn't exist."));
        if (issue is not null && parent.Id == issue.Id)
            throw new BoardRuleException(L.T("An issue can't be its own parent."));
        switch (level)
        {
            case IssueTypeLevel.Epic:
                throw new BoardRuleException(L.T("An epic can't have a parent."));
            case IssueTypeLevel.Standard when board.LevelOf(parent) != IssueTypeLevel.Epic:
                throw new BoardRuleException(L.T($"{parent.Key} isn't an epic: only epics can hold issues."));
            case IssueTypeLevel.Subtask when board.LevelOf(parent) != IssueTypeLevel.Standard:
                throw new BoardRuleException(L.T($"A sub-task's parent must be a standard issue, and {parent.Key} isn't one."));
        }
    }

    private static void ValidateType(BoardSnapshot board, Issue issue, string? typeId)
    {
        var type = board.Project.IssueType(typeId) ?? throw new BoardRuleException(L.T($"There's no issue type \"{typeId}\"."));
        var oldLevel = board.LevelOf(issue);
        if (type.Level != oldLevel && board.ChildrenOf(issue.Id).Count > 0)
            throw new BoardRuleException(oldLevel == IssueTypeLevel.Epic
                ? L.T("This epic still has issues: move them out first.")
                : L.T("This issue still has sub-tasks: move or delete them first."));
        ValidateParent(board, issue, type.Id, issue.Parent);
    }

    private static void ValidateCustom(BoardSnapshot board, string id, string? text)
    {
        var definition = board.Project.CustomField(id) ?? throw new BoardRuleException(L.T($"There's no custom field \"{id}\"."));
        if (text is null)
            return;
        switch (definition.Type)
        {
            case FieldType.Number when !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _):
                throw new BoardRuleException(L.T($"{definition.Name} must be a number."));
            case FieldType.Date when !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                throw new BoardRuleException(L.T($"{definition.Name} must be a date like 2026-10-31."));
            case FieldType.Select when !definition.Options.Contains(text):
                throw new BoardRuleException(L.T($"\"{text}\" isn't one of {definition.Name}'s choices."));
            case FieldType.Person when board.Project.Person(text) is null:
                throw new BoardRuleException(L.T($"There's nobody with the id \"{text}\" on this board."));
        }
    }

    private static IReadOnlyList<string> Items(FieldValue value) => value switch
    {
        FieldValue.Items i => i.Values,
        FieldValue.Text { Value: { Length: > 0 } t } => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => [],
    };

    private static IReadOnlyList<string> Distinct(IReadOnlyList<string> values) =>
        values.Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    // Labels keep their case but are unique ignoring it, sorted, so the same set always looks the same.
    private static IReadOnlyList<string> NormalizeLabels(IReadOnlyList<string> values) =>
        values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
}
