using System.Globalization;
using DevHub.Board.Core.Model;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Core.Search;

/// <summary>What a query runs against: the board, who "currentUser()" is, and what "now" is.</summary>
public sealed record QueryContext(BoardSnapshot Board, string? CurrentUser, DateTimeOffset Now)
{
    /// <summary>The time zone days and weeks are counted in (startOfDay(), "created = 2026-10-01").</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>First day of a week for startOfWeek().</summary>
    public DayOfWeek FirstDayOfWeek { get; init; } = DayOfWeek.Monday;
}

/// <summary>A query ready to run: a test for one issue and the order to list matches in.</summary>
public sealed class CompiledQuery(Func<Issue, bool> matches, IComparer<Issue> order)
{
    public Func<Issue, bool> Matches { get; } = matches;
    public IComparer<Issue> Order { get; } = order;

    /// <summary>The matching issues, in the query's order.</summary>
    public IReadOnlyList<Issue> Run(IEnumerable<Issue> issues) => issues.Where(Matches).Order(Order).ToList();
}

/// <summary>
/// Turns a <see cref="Query"/> into a <see cref="CompiledQuery"/> for one board. Values are names or ids,
/// case-insensitive (<c>status = "in progress"</c>); an unknown field or value is an error at its position.
/// </summary>
/// <remarks>
/// Fields: key, project, type, status, statusCategory, priority, resolution, assignee, reporter, labels,
/// components, fixVersion, sprint, parent, epic, summary, description, comment, text, points, originalEstimate,
/// remainingEstimate, timeSpent, created, updated, resolved, due, rank (ORDER BY only) and custom fields by name.
/// Functions: currentUser(), openSprints(), closedSprints(), futureSprints(), now(), startOfDay(), endOfDay(),
/// startOfWeek(), endOfWeek(), startOfMonth(), endOfMonth() (each takes an optional offset: startOfDay(-1)).
/// Dates: 2026-10-31, or relative to now: -1d, -2w, +3h, -30m.
/// </remarks>
public static class QueryCompiler
{
    public static CompiledQuery Compile(string? text, QueryContext context) => Compile(QueryParser.Parse(text), context);

    public static CompiledQuery Compile(Query query, QueryContext context)
    {
        var matches = query.Where is null ? (_ => true) : Build(query.Where, context);
        return new CompiledQuery(matches, BuildOrder(query.OrderBy, context));
    }

    /// <summary>Field names offered by completion, in a friendly order.</summary>
    public static IReadOnlyList<string> FieldNames { get; } =
    [
        "assignee", "comment", "component", "created", "criteria", "description", "due", "epic", "fixVersion", "key",
        "labels", "openCriteria", "openDone", "originalEstimate", "parent", "points", "priority", "project",
        "remainingEstimate", "reporter", "resolution", "resolved", "sprint", "status", "statusCategory", "summary",
        "text", "timeSpent", "type", "updated",
    ];

    /// <summary>Functions offered by completion.</summary>
    public static IReadOnlyList<string> FunctionNames { get; } =
    [
        "currentUser()", "openSprints()", "closedSprints()", "futureSprints()", "now()", "startOfDay()", "endOfDay()",
        "startOfWeek()", "endOfWeek()", "startOfMonth()", "endOfMonth()",
    ];

    private enum FieldKind { Key, Project, Ref, Multi, Text, Number, Date, Category, Epic }

    private sealed record Field(string Name, FieldKind Kind, Func<Issue, string?>? One = null, Func<Issue, IEnumerable<string>>? Many = null,
        Func<Issue, double?>? Number = null, Func<Issue, DateTimeOffset?>? Date = null, Func<string, QueryContext, IEnumerable<string>>? Resolve = null,
        Func<Issue, IEnumerable<string>>? Texts = null, bool Durations = false, FieldDefinition? Custom = null);

    // ---- Conditions ---------------------------------------------------------------------------------------

    private static Func<Issue, bool> Build(QueryNode node, QueryContext context)
    {
        switch (node)
        {
            case QueryNode.And and:
            {
                var l = Build(and.Left, context);
                var r = Build(and.Right, context);
                return i => l(i) && r(i);
            }
            case QueryNode.Or or:
            {
                var l = Build(or.Left, context);
                var r = Build(or.Right, context);
                return i => l(i) || r(i);
            }
            case QueryNode.Not not:
            {
                var inner = Build(not.Inner, context);
                return i => !inner(i);
            }
            case QueryNode.Empty empty:
            {
                var field = FieldFor(empty.Field, context, empty.Position);
                var isEmpty = EmptyTest(field);
                return empty.Negated ? i => !isEmpty(i) : isEmpty;
            }
            case QueryNode.In @in:
            {
                var field = FieldFor(@in.Field, context, @in.Position);
                var tests = @in.Values.Select(v => Equality(field, v, context, @in.Position)).ToList();
                return @in.Negated ? i => !tests.Any(t => t(i)) : i => tests.Any(t => t(i));
            }
            case QueryNode.Compare compare:
                return BuildCompare(compare, context);
            default:
                throw new QueryException(L.T("Unknown condition."), 0);
        }
    }

    private static Func<Issue, bool> BuildCompare(QueryNode.Compare compare, QueryContext context)
    {
        var field = FieldFor(compare.Field, context, compare.Position);
        switch (compare.Op)
        {
            case "=":
                return Equality(field, compare.Value, context, compare.Position);
            case "!=":
            {
                var equal = Equality(field, compare.Value, context, compare.Position);
                return i => !equal(i);
            }
            case "~" or "!~":
            {
                if (field.Kind != FieldKind.Text)
                    throw new QueryException(L.T($"~ (contains) only works on text fields like summary, not {field.Name}."), compare.Position);
                var words = LiteralText(compare.Value, compare.Position)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                bool Contains(Issue i)
                {
                    var texts = field.Texts!(i).ToList();
                    return words.All(w => texts.Any(t => t.Contains(w, StringComparison.CurrentCultureIgnoreCase)));
                }
                return compare.Op == "~" ? Contains : i => !Contains(i);
            }
            default:
                return Ordering(field, compare, context);
        }
    }

    private static Func<Issue, bool> Equality(Field field, QueryValue value, QueryContext context, int position)
    {
        switch (field.Kind)
        {
            case FieldKind.Project:
            {
                var key = LiteralText(value, position);
                var same = string.Equals(key, context.Board.Project.Key, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(key, context.Board.Project.Name, StringComparison.OrdinalIgnoreCase);
                return _ => same;
            }
            case FieldKind.Key:
            {
                var issue = context.Board.IssueByKey(LiteralText(value, position)) ?? context.Board.Issue(LiteralText(value, position));
                return issue is null ? _ => false : i => i.Id == issue.Id;
            }
            case FieldKind.Ref:
            {
                var ids = field.Resolve!(ValueKey(value, field, context, position), context).ToHashSet(StringComparer.Ordinal);
                return i => field.One!(i) is { } v && ids.Contains(v);
            }
            case FieldKind.Multi:
            {
                var ids = field.Resolve!(ValueKey(value, field, context, position), context).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return i => field.Many!(i).Any(ids.Contains);
            }
            case FieldKind.Epic:
            {
                var ids = field.Resolve!(ValueKey(value, field, context, position), context).ToHashSet(StringComparer.Ordinal);
                return i => context.Board.EpicOf(i) is { } epic && ids.Contains(epic.Id);
            }
            case FieldKind.Category:
            {
                var category = ParseCategory(LiteralText(value, position), position);
                return i => context.Board.CategoryOf(i) == category;
            }
            case FieldKind.Text:
            {
                var text = LiteralText(value, position);
                return i => field.Texts!(i).Any(t => string.Equals(t, text, StringComparison.CurrentCultureIgnoreCase));
            }
            case FieldKind.Number:
            {
                var number = ParseNumber(field, value, context, position);
                return i => field.Number!(i) is { } n && Math.Abs(n - number) < 1e-9;
            }
            case FieldKind.Date:
            {
                var (from, to) = DayOf(ParseDate(value, context, position), context);
                return i => field.Date!(i) is { } d && d >= from && d < to;
            }
            default:
                throw new QueryException(L.T($"{field.Name} can't be compared that way."), position);
        }
    }

    private static Func<Issue, bool> Ordering(Field field, QueryNode.Compare compare, QueryContext context)
    {
        var op = compare.Op;
        Func<int, bool> accept = op switch
        {
            ">" => c => c > 0,
            ">=" => c => c >= 0,
            "<" => c => c < 0,
            "<=" => c => c <= 0,
            _ => throw new QueryException(L.T($"Unknown operator {op}."), compare.Position),
        };
        switch (field.Kind)
        {
            case FieldKind.Number:
            {
                var number = ParseNumber(field, compare.Value, context, compare.Position);
                return i => field.Number!(i) is { } n && accept(n.CompareTo(number));
            }
            case FieldKind.Date:
            {
                var date = ParseDate(compare.Value, context, compare.Position);
                return i => field.Date!(i) is { } d && accept(d.CompareTo(date));
            }
            case FieldKind.Key:
            {
                if (!IssueKey.TryParse(LiteralText(compare.Value, compare.Position).ToUpperInvariant(), out _, out var number))
                    throw new QueryException(L.T("Compare keys with a key like DEV-12."), compare.Position);
                return i => IssueKey.TryParse(i.Key, out _, out var n) && accept(n.CompareTo(number));
            }
            case FieldKind.Ref when field.Name == "priority":
            {
                // Higher priority = earlier in the list: "priority > Medium" means High and Highest.
                var priorities = context.Board.Project.Priorities;
                var target = field.Resolve!(LiteralText(compare.Value, compare.Position), context).FirstOrDefault();
                var targetIndex = priorities.ToList().FindIndex(p => p.Id == target);
                return i =>
                {
                    var index = priorities.ToList().FindIndex(p => p.Id == i.Priority);
                    return index >= 0 && accept(targetIndex.CompareTo(index));
                };
            }
            default:
                throw new QueryException(L.T($"{field.Name} can't be compared with {op}."), compare.Position);
        }
    }

    private static Func<Issue, bool> EmptyTest(Field field) => field.Kind switch
    {
        FieldKind.Ref => i => field.One!(i) is null,
        FieldKind.Multi => i => !field.Many!(i).Any(),
        FieldKind.Text => i => field.Texts!(i).All(string.IsNullOrWhiteSpace),
        FieldKind.Number => i => field.Number!(i) is null,
        FieldKind.Date => i => field.Date!(i) is null,
        FieldKind.Epic => i => i.Parent is null,
        _ => _ => false,
    };

    // ---- Fields -------------------------------------------------------------------------------------------

    private static string Normalize(string name) => name.Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();

    private static Field FieldFor(string name, QueryContext context, int position)
    {
        var board = context.Board;
        var project = board.Project;
        switch (Normalize(name))
        {
            case "key" or "issuekey" or "id" or "issue":
                return new Field("key", FieldKind.Key);
            case "project":
                return new Field("project", FieldKind.Project);
            case "type" or "issuetype":
                return new Field("type", FieldKind.Ref, One: i => i.Type,
                    Resolve: (v, c) => ById(project.IssueTypes, t => t.Id, t => t.Name, v, L.T("issue type"), position));
            case "status":
                return new Field("status", FieldKind.Ref, One: i => i.Status,
                    Resolve: (v, c) => ById(project.Statuses, s => s.Id, s => s.Name, v, "status", position));
            case "statuscategory" or "category":
                return new Field("statusCategory", FieldKind.Category);
            case "priority":
                return new Field("priority", FieldKind.Ref, One: i => i.Priority,
                    Resolve: (v, c) => ById(project.Priorities, p => p.Id, p => p.Name, v, "priority", position));
            case "resolution":
                return new Field("resolution", FieldKind.Ref, One: i => i.Resolution,
                    Resolve: (v, c) => ById(project.Resolutions, r => r.Id, r => r.Name, v, "resolution", position));
            case "assignee":
                return new Field("assignee", FieldKind.Ref, One: i => i.Assignee, Resolve: (v, c) => People(v, c, position));
            case "reporter":
                return new Field("reporter", FieldKind.Ref, One: i => i.Reporter, Resolve: (v, c) => People(v, c, position));
            case "label" or "labels":
                return new Field("labels", FieldKind.Multi, Many: i => i.Labels, Resolve: (v, _) => [v]);
            case "component" or "components":
                return new Field("component", FieldKind.Multi, Many: i => i.Components,
                    Resolve: (v, c) => ById(project.Components, x => x.Id, x => x.Name, v, "component", position));
            case "fixversion" or "fixversions" or "version":
                return new Field("fixVersion", FieldKind.Multi, Many: i => i.FixVersions,
                    Resolve: (v, c) => ById(board.Versions.Values, x => x.Id, x => x.Name, v, "version", position));
            case "sprint":
                return new Field("sprint", FieldKind.Ref, One: i => i.Sprint, Resolve: (v, c) => Sprints(v, c, position));
            case "parent":
                return new Field("parent", FieldKind.Ref, One: i => i.Parent, Resolve: (v, c) => Issues(v, c, position));
            case "epic" or "epiclink":
                return new Field("epic", FieldKind.Epic, Resolve: (v, c) => Issues(v, c, position));
            case "summary" or "title":
                return new Field("summary", FieldKind.Text, Texts: i => [i.Summary]);
            case "description":
                return new Field("description", FieldKind.Text, Texts: i => [i.Description]);
            case "comment" or "comments":
                return new Field("comment", FieldKind.Text, Texts: i => i.Comments.Select(c => c.Body));
            case "text":
                return new Field("text", FieldKind.Text, Texts: i => [i.Summary, i.Description, .. i.Comments.Select(c => c.Body)]);
            // Acceptance criteria: their text, and how many are still unticked ("openCriteria > 0"); "openDone" counts
            // unticked Definition of Done items (standard issues only, like the done check).
            case "criteria" or "acceptancecriteria":
                return new Field("criteria", FieldKind.Text, Texts: i => i.Criteria.Select(c => c.Text));
            case "opencriteria":
                return new Field("openCriteria", FieldKind.Number, Number: i => i.Criteria.Count(c => !c.Done));
            case "opendone" or "opendod":
                return new Field("openDone", FieldKind.Number, Number: i => context.Board.LevelOf(i) == IssueTypeLevel.Standard
                    ? context.Board.Project.DefinitionOfDone.Count(d => !i.DoneChecks.Contains(d.Id)) : 0);
            case "points" or "storypoints" or "storypointestimate" or "sp":
                return new Field("points", FieldKind.Number, Number: i => i.StoryPoints);
            case "originalestimate":
                return new Field("originalEstimate", FieldKind.Number, Number: i => i.Time.OriginalMinutes, Durations: true);
            case "remainingestimate":
                return new Field("remainingEstimate", FieldKind.Number, Number: i => i.Time.RemainingMinutes, Durations: true);
            case "timespent":
                return new Field("timeSpent", FieldKind.Number, Number: i => i.Time.Log.Count == 0 ? null : i.Time.SpentMinutes, Durations: true);
            case "created" or "createddate":
                return new Field("created", FieldKind.Date, Date: i => i.Created);
            case "updated" or "updateddate":
                return new Field("updated", FieldKind.Date, Date: i => i.Updated);
            case "resolved" or "resolutiondate":
                return new Field("resolved", FieldKind.Date, Date: i => i.Resolved);
            case "due" or "duedate":
                return new Field("due", FieldKind.Date, Date: i => i.Due is { } d ? StartOfDay(d, context) : null);
        }

        // A custom field, by name or id.
        var custom = project.CustomFields.FirstOrDefault(f => Normalize(f.Name) == Normalize(name))
                     ?? project.CustomFields.FirstOrDefault(f => f.Id == name);
        if (custom is not null)
            return CustomField(custom, context, position);
        throw new QueryException(L.T($"There's no field \"{name}\"."), position);
    }

    private static Field CustomField(FieldDefinition custom, QueryContext context, int position)
    {
        string? Raw(Issue i) => i.Custom.GetValueOrDefault(custom.Id);
        return custom.Type switch
        {
            FieldType.Number => new Field(custom.Name, FieldKind.Number, Custom: custom,
                Number: i => double.TryParse(Raw(i), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null),
            FieldType.Date => new Field(custom.Name, FieldKind.Date, Custom: custom,
                Date: i => DateOnly.TryParseExact(Raw(i), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? StartOfDay(d, context) : null),
            FieldType.Person => new Field(custom.Name, FieldKind.Ref, One: Raw, Custom: custom, Resolve: (v, c) => People(v, c, position)),
            FieldType.Select => new Field(custom.Name, FieldKind.Ref, One: Raw, Custom: custom,
                Resolve: (v, _) => custom.Options.Where(o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty()
                    .Select(o => o ?? throw new QueryException(L.T($"\"{v}\" isn't one of {custom.Name}'s choices."), position)).ToList()),
            _ => new Field(custom.Name, FieldKind.Text, Custom: custom, Texts: i => Raw(i) is { } t ? [t] : []),
        };
    }

    private static IEnumerable<string> ById<T>(IEnumerable<T> items, Func<T, string> id, Func<T, string> name, string value, string what, int position)
    {
        var list = items.ToList();
        var found = list.Where(i => string.Equals(id(i), value, StringComparison.Ordinal)
                                    || string.Equals(name(i), value, StringComparison.OrdinalIgnoreCase)).Select(id).ToList();
        if (found.Count == 0)
            throw new QueryException(L.T($"There's no {what} \"{value}\"."), position);
        return found;
    }

    private static IEnumerable<string> People(string value, QueryContext context, int position)
    {
        if (value == CurrentUserMarker)
            return context.CurrentUser is null ? [] : [context.CurrentUser];
        var byEmail = context.Board.Project.People.Where(p => string.Equals(p.Email, value, StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToList();
        return byEmail.Count > 0 ? byEmail : ById(context.Board.Project.People, p => p.Id, p => p.Name, value, "person", position);
    }

    private static IEnumerable<string> Sprints(string value, QueryContext context, int position)
    {
        var sprints = context.Board.Sprints.Values;
        return value switch
        {
            OpenSprintsMarker => sprints.Where(s => s.State == SprintState.Active).Select(s => s.Id).ToList(),
            ClosedSprintsMarker => sprints.Where(s => s.State == SprintState.Closed).Select(s => s.Id).ToList(),
            FutureSprintsMarker => sprints.Where(s => s.State == SprintState.Future).Select(s => s.Id).ToList(),
            _ => ById(sprints, s => s.Id, s => s.Name, value, "sprint", position),
        };
    }

    private static IEnumerable<string> Issues(string value, QueryContext context, int position) =>
        (context.Board.IssueByKey(value) ?? context.Board.Issue(value)) is { } issue
            ? [issue.Id]
            : throw new QueryException(L.T($"There's no issue \"{value}\"."), position);

    // ---- Values -------------------------------------------------------------------------------------------

    private const string CurrentUserMarker = "\u0001currentUser";
    private const string OpenSprintsMarker = "\u0001openSprints";
    private const string ClosedSprintsMarker = "\u0001closedSprints";
    private const string FutureSprintsMarker = "\u0001futureSprints";

    // The text a ref field resolves: a literal, or a marker for the functions that stand for a set.
    private static string ValueKey(QueryValue value, Field field, QueryContext context, int position)
    {
        var isPerson = field.Name is "assignee" or "reporter" || field.Custom?.Type == FieldType.Person;
        return value switch
        {
            QueryValue.Literal l => l.Text,
            QueryValue.Function f => Normalize(f.Name) switch
            {
                "currentuser" or "me" when isPerson => CurrentUserMarker,
                "opensprints" when field.Name == "sprint" => OpenSprintsMarker,
                "closedsprints" when field.Name == "sprint" => ClosedSprintsMarker,
                "futuresprints" when field.Name == "sprint" => FutureSprintsMarker,
                _ => throw new QueryException(L.T($"{f.Name}() can't be used with {field.Name}."), position),
            },
            _ => throw new QueryException(L.T("Expected a value."), position),
        };
    }

    private static string LiteralText(QueryValue value, int position) => value switch
    {
        QueryValue.Literal l => l.Text,
        QueryValue.Function f => throw new QueryException(L.T($"{f.Name}() can't be used here."), position),
        _ => throw new QueryException(L.T("Expected a value."), position),
    };

    private static double ParseNumber(Field field, QueryValue value, QueryContext context, int position)
    {
        var text = LiteralText(value, position);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return number;
        if (field.Durations && WorkDuration.TryParse(text, context.Board.Project.Settings, out var minutes))
            return minutes;
        throw new QueryException(field.Durations
            ? L.T($"{field.Name} takes a duration like 2h or 1d 4h.")
            : L.T($"{field.Name} takes a number, not \"{text}\"."), position);
    }

    private static StatusCategory ParseCategory(string text, int position) => Normalize(text) switch
    {
        "todo" or "new" or "open" => StatusCategory.ToDo,
        "inprogress" or "indeterminate" => StatusCategory.InProgress,
        "done" or "complete" or "closed" => StatusCategory.Done,
        _ => throw new QueryException(L.T($"A status category is \"To Do\", \"In Progress\" or \"Done\", not \"{text}\"."), position),
    };

    private static DateTimeOffset ParseDate(QueryValue value, QueryContext context, int position)
    {
        var local = TimeZoneInfo.ConvertTime(context.Now, context.TimeZone);
        if (value is QueryValue.Function f)
        {
            var offset = f.Arguments.Count > 0 && int.TryParse(f.Arguments[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var o) ? o : 0;
            var today = DateOnly.FromDateTime(local.DateTime);
            return Normalize(f.Name) switch
            {
                "now" => context.Now,
                "startofday" => StartOfDay(today.AddDays(offset), context),
                "endofday" => StartOfDay(today.AddDays(offset + 1), context).AddTicks(-1),
                "startofweek" => StartOfDay(WeekStart(today, context).AddDays(7 * offset), context),
                "endofweek" => StartOfDay(WeekStart(today, context).AddDays(7 * (offset + 1)), context).AddTicks(-1),
                "startofmonth" => StartOfDay(new DateOnly(today.Year, today.Month, 1).AddMonths(offset), context),
                "endofmonth" => StartOfDay(new DateOnly(today.Year, today.Month, 1).AddMonths(offset + 1), context).AddTicks(-1),
                _ => throw new QueryException(L.T($"{f.Name}() isn't a date function."), position),
            };
        }

        var text = LiteralText(value, position).Trim();
        if (DateOnly.TryParseExact(text, ["yyyy-MM-dd", "yyyy/MM/dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return StartOfDay(date, context);
        if (text.Length >= 2 && text[0] is '-' or '+' && char.IsAsciiLetter(text[^1])
            && double.TryParse(text[1..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            var sign = text[0] == '-' ? -1 : 1;
            var span = char.ToLowerInvariant(text[^1]) switch
            {
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                'w' => TimeSpan.FromDays(7 * amount),
                _ => throw new QueryException(L.T($"\"{text}\": use m, h, d or w (like -2w)."), position),
            };
            return context.Now + sign * span;
        }
        throw new QueryException(L.T($"\"{text}\" isn't a date: use 2026-10-31, -1d, or a function like startOfWeek()."), position);
    }

    private static DateOnly WeekStart(DateOnly day, QueryContext context)
    {
        var diff = ((int)day.DayOfWeek - (int)context.FirstDayOfWeek + 7) % 7;
        return day.AddDays(-diff);
    }

    private static DateTimeOffset StartOfDay(DateOnly day, QueryContext context)
    {
        var dateTime = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(dateTime, context.TimeZone.GetUtcOffset(dateTime));
    }

    // The local day containing an instant.
    private static (DateTimeOffset From, DateTimeOffset To) DayOf(DateTimeOffset instant, QueryContext context)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, context.TimeZone).DateTime);
        return (StartOfDay(day, context), StartOfDay(day.AddDays(1), context));
    }

    // ---- Order --------------------------------------------------------------------------------------------

    private static IComparer<Issue> BuildOrder(IReadOnlyList<QueryOrder> order, QueryContext context)
    {
        var keys = order.Select(o => (Key: SortKey(o.Field, context), o.Descending)).ToList();
        if (keys.Count == 0)
            keys.Add((SortKey("rank", context), false));
        return Comparer<Issue>.Create((a, b) =>
        {
            foreach (var (key, descending) in keys)
            {
                var c = key(a, b);
                if (c != 0)
                    return descending ? -c : c;
            }
            return KeyNumber(a).CompareTo(KeyNumber(b));
        });
    }

    private static int KeyNumber(Issue issue) => IssueKey.TryParse(issue.Key, out _, out var n) ? n : int.MaxValue;

    // Empty values sort last either way round would need two passes; they sort first ascending, like Jira's nulls.
    private static Comparison<Issue> SortKey(string field, QueryContext context)
    {
        var board = context.Board;
        var project = board.Project;
        switch (Normalize(field))
        {
            case "rank":
                return (a, b) => string.CompareOrdinal(a.Rank, b.Rank);
            case "key" or "issuekey":
                return (a, b) => KeyNumber(a).CompareTo(KeyNumber(b));
            case "priority":
                var priorities = project.Priorities.Select(p => p.Id).ToList();
                // Highest first when ascending would surprise: Jira's "ORDER BY priority" puts the highest first too.
                return (a, b) => Index(priorities, a.Priority).CompareTo(Index(priorities, b.Priority));
            case "status":
                var workflowOrder = (board.DefaultWorkflow?.Statuses ?? []).ToList();
                return (a, b) => Index(workflowOrder, a.Status).CompareTo(Index(workflowOrder, b.Status));
            case "statuscategory":
                return (a, b) => board.CategoryOf(a).CompareTo(board.CategoryOf(b));
            case "type" or "issuetype":
                return (a, b) => string.Compare(project.IssueType(a.Type)?.Name, project.IssueType(b.Type)?.Name, StringComparison.CurrentCultureIgnoreCase);
            case "summary":
                return (a, b) => string.Compare(a.Summary, b.Summary, StringComparison.CurrentCultureIgnoreCase);
            case "assignee":
                return (a, b) => string.Compare(project.Person(a.Assignee)?.Name, project.Person(b.Assignee)?.Name, StringComparison.CurrentCultureIgnoreCase);
            case "reporter":
                return (a, b) => string.Compare(project.Person(a.Reporter)?.Name, project.Person(b.Reporter)?.Name, StringComparison.CurrentCultureIgnoreCase);
            case "points" or "storypoints":
                return (a, b) => Nullable.Compare(a.StoryPoints, b.StoryPoints);
            case "created":
                return (a, b) => a.Created.CompareTo(b.Created);
            case "updated":
                return (a, b) => a.Updated.CompareTo(b.Updated);
            case "resolved":
                return (a, b) => Nullable.Compare(a.Resolved, b.Resolved);
            case "due" or "duedate":
                return (a, b) => Nullable.Compare(a.Due, b.Due);
            case "originalestimate":
                return (a, b) => Nullable.Compare(a.Time.OriginalMinutes, b.Time.OriginalMinutes);
            case "remainingestimate":
                return (a, b) => Nullable.Compare(a.Time.RemainingMinutes, b.Time.RemainingMinutes);
            default:
                throw new QueryException(L.T($"Results can't be ordered by \"{field}\"."), 0);
        }
    }

    private static int Index(List<string> ids, string? id) => id is null ? int.MaxValue : ids.IndexOf(id) is var i and >= 0 ? i : int.MaxValue - 1;
}
