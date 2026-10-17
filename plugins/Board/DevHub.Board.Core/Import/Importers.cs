using System.Globalization;
using System.Text.Json;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Core.Import;

/// <summary>A comment brought in from elsewhere.</summary>
public sealed record ImportedComment(string? Author, DateTimeOffset? Created, string Body);

/// <summary>
/// An issue read from another tool, with names (not ids): <see cref="ImportIssues"/> maps them onto the board.
/// <see cref="ExternalKey"/> (PROJ-12, #42) is kept as an old key, so references to it still find the issue.
/// </summary>
public sealed record ImportedIssue
{
    public string? ExternalKey { get; init; }
    public string Type { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Status { get; init; }
    public string? Priority { get; init; }
    public string? Resolution { get; init; }
    public string? Assignee { get; init; }
    public string? Reporter { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<string> Components { get; init; } = [];
    public IReadOnlyList<string> FixVersions { get; init; } = [];
    public double? StoryPoints { get; init; }
    public int? OriginalMinutes { get; init; }
    public int? RemainingMinutes { get; init; }
    public int? SpentMinutes { get; init; }
    public DateOnly? Due { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Resolved { get; init; }
    public string? ParentKey { get; init; }
    public IReadOnlyList<ImportedComment> Comments { get; init; } = [];
}

/// <summary>What an import would do, for the preview.</summary>
public sealed record ImportPreview(
    int Issues,
    IReadOnlyDictionary<string, string> Types,
    IReadOnlyDictionary<string, string> Statuses,
    IReadOnlyList<string> NewPeople,
    IReadOnlyList<string> NewComponents,
    IReadOnlyList<string> NewVersions,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads Jira's CSV export ("Export → CSV (all fields)"). Columns repeat for multi-value fields (Labels, Component/s,
/// Fix Version/s, Comment, Sprint); story points come from whichever of Jira's story point fields is there; times are
/// in seconds; dates in Jira's "dd/MMM/yy h:mm tt" style (or ISO).
/// </summary>
public static class JiraCsvImport
{
    public static IReadOnlyList<ImportedIssue> Read(string csv, List<string>? warnings = null)
    {
        var rows = Csv.Read(csv);
        if (rows.Count < 2)
            return [];
        var header = rows[0].Select(h => h.Trim()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Any(n => string.Equals(h, n, StringComparison.OrdinalIgnoreCase)));
        IReadOnlyList<int> Cols(params string[] names) =>
            header.Select((h, i) => (h, i)).Where(x => names.Any(n => string.Equals(x.h, n, StringComparison.OrdinalIgnoreCase))).Select(x => x.i).ToList();

        var key = Col("Issue key", "Key");
        var summary = Col("Summary");
        if (summary < 0)
            throw new FormatException(L.T("This doesn't look like a Jira CSV export: there's no Summary column."));
        var type = Col("Issue Type", "Issuetype", "Type");
        var status = Col("Status");
        var priority = Col("Priority");
        var resolution = Col("Resolution");
        var assignee = Col("Assignee");
        var reporter = Col("Reporter");
        var description = Col("Description");
        var created = Col("Created");
        var resolved = Col("Resolved", "Resolution date");
        var due = Col("Due Date", "Due date");
        var points = Col("Custom field (Story Points)", "Custom field (Story point estimate)", "Story Points", "Story point estimate");
        var original = Col("Original Estimate", "Original estimate");
        var remaining = Col("Remaining Estimate", "Remaining estimate");
        var spent = Col("Time Spent", "Time spent");
        var parent = Col("Parent", "Parent key", "Custom field (Epic Link)", "Epic Link", "Parent summary");
        var parentId = Col("Parent id");
        var issueId = Col("Issue id");
        var labels = Cols("Labels");
        var components = Cols("Component/s", "Components");
        var versions = Cols("Fix Version/s", "Fix versions");
        var comments = Cols("Comment");

        // Parent id (a number) → that issue's key.
        var keyById = issueId < 0 || key < 0 ? new Dictionary<string, string>()
            : rows.Skip(1).Where(r => r.Count > Math.Max(issueId, key)).GroupBy(r => r[issueId]).ToDictionary(g => g.Key, g => g.First()[key]);

        var issues = new List<ImportedIssue>();
        foreach (var row in rows.Skip(1))
        {
            string? Get(int i) => i >= 0 && i < row.Count && row[i].Trim().Length > 0 ? row[i].Trim() : null;
            IReadOnlyList<string> All(IReadOnlyList<int> columns) => columns.Select(Get).OfType<string>().ToList();
            var title = Get(summary);
            if (title is null)
                continue;
            var parentKey = Get(parent);
            if (parentKey is null && Get(parentId) is { } pid)
                parentKey = keyById.GetValueOrDefault(pid);
            issues.Add(new ImportedIssue
            {
                ExternalKey = Get(key),
                Type = Get(type) ?? "Task",
                Summary = title,
                Description = Get(description) ?? "",
                Status = Get(status),
                Priority = Get(priority),
                Resolution = Get(resolution),
                Assignee = Get(assignee),
                Reporter = Get(reporter),
                Labels = All(labels),
                Components = All(components),
                FixVersions = All(versions),
                StoryPoints = double.TryParse(Get(points), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null,
                OriginalMinutes = Seconds(Get(original)),
                RemainingMinutes = Seconds(Get(remaining)),
                SpentMinutes = Seconds(Get(spent)),
                Due = Date(Get(due)) is { } d ? DateOnly.FromDateTime(d.LocalDateTime) : null,
                Created = Date(Get(created)),
                Resolved = Date(Get(resolved)),
                ParentKey = parentKey,
                Comments = All(comments).Select(Comment).ToList(),
            });
        }
        return issues;
    }

    private static int? Seconds(string? text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s > 0 ? (int)Math.Round(s / 60.0) : null;

    private static readonly string[] JiraDateFormats =
        ["dd/MMM/yy h:mm tt", "dd/MMM/yy H:mm", "d/MMM/yy h:mm tt", "dd/MMM/yyyy h:mm tt", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "dd/MMM/yy"];

    internal static DateTimeOffset? Date(string? text)
    {
        if (text is null)
            return null;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var iso) && text.Contains('-'))
            return iso;
        return DateTime.TryParseExact(text, JiraDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var jira)
            ? new DateTimeOffset(jira)
            : null;
    }

    // Jira writes comments as "date;author;text".
    private static ImportedComment Comment(string text)
    {
        var parts = text.Split(';', 3);
        return parts.Length == 3 && Date(parts[0].Trim()) is { } when
            ? new ImportedComment(parts[1].Trim(), when, parts[2].Trim())
            : new ImportedComment(null, null, text);
    }
}

/// <summary>Reads GitHub's REST issues JSON (pages of <c>GET /repos/{owner}/{repo}/issues?state=all</c>); pull requests are left out.</summary>
public static class GitHubIssuesImport
{
    public static IReadOnlyList<ImportedIssue> Read(IEnumerable<string> pages)
    {
        var issues = new List<ImportedIssue>();
        foreach (var page in pages)
        {
            using var json = JsonDocument.Parse(page);
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("pull_request", out _))
                    continue;
                var labels = item.TryGetProperty("labels", out var l)
                    ? l.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetProperty("name").GetString()).OfType<string>().ToList()
                    : [];
                var closed = item.GetProperty("state").GetString() == "closed";
                var reason = item.TryGetProperty("state_reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                string? Login(string name) => item.TryGetProperty(name, out var u) && u.ValueKind == JsonValueKind.Object ? u.GetProperty("login").GetString() : null;
                DateTimeOffset? When(string name) => item.TryGetProperty(name, out var d) && d.ValueKind == JsonValueKind.String ? d.GetDateTimeOffset() : null;
                var milestone = item.TryGetProperty("milestone", out var m) && m.ValueKind == JsonValueKind.Object ? m.GetProperty("title").GetString() : null;
                issues.Add(new ImportedIssue
                {
                    ExternalKey = "#" + item.GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture),
                    Type = labels.Any(x => x.Contains("bug", StringComparison.OrdinalIgnoreCase)) ? "Bug" : "Story",
                    Summary = item.GetProperty("title").GetString() ?? "",
                    Description = item.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "",
                    Status = closed ? "Done" : "To Do",
                    Resolution = closed ? (reason == "not_planned" ? "Won't Do" : "Done") : null,
                    Assignee = Login("assignee"),
                    Reporter = Login("user"),
                    Labels = labels.Where(x => !x.Equals("bug", StringComparison.OrdinalIgnoreCase)).ToList(),
                    FixVersions = milestone is null ? [] : [milestone],
                    Created = When("created_at"),
                    Resolved = closed ? When("closed_at") : null,
                });
            }
        }
        return issues;
    }
}

/// <summary>
/// Creates imported issues: names are matched to the board's types, statuses, priorities, resolutions, people,
/// components and versions (case doesn't matter); missing people, components and versions are added; unknown types
/// and statuses map to the closest kind (an "Epic" type to the epic level, a "Done"-like status to a Done status…).
/// Parents are linked when the hierarchy allows. Creation and resolution times are kept, for the reports.
/// </summary>
public sealed record ImportIssues(IReadOnlyList<ImportedIssue> Issues, string Source) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context) => Build(board, context, null);

    /// <summary>What <see cref="Apply"/> would do, without doing it.</summary>
    public ImportPreview Preview(BoardSnapshot board, OperationContext context)
    {
        var preview = new PreviewCollector();
        Build(board, context, preview);
        return preview.ToPreview(Issues.Count);
    }

    private sealed class PreviewCollector
    {
        public Dictionary<string, string> Types { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Statuses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedSet<string> People { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedSet<string> Components { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedSet<string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings { get; } = [];
        public ImportPreview ToPreview(int count) => new(count, Types, Statuses, People.ToList(), Components.ToList(), Versions.ToList(), Warnings.Distinct().ToList());
    }

    private BoardChange Build(BoardSnapshot board, OperationContext context, PreviewCollector? preview)
    {
        if (Issues.Count == 0)
            throw new BoardRuleException(L.T("There's nothing to import."));
        var change = new ChangeBuilder(board, context);
        var project = board.Project;
        var people = project.People.ToList();
        var components = project.Components.ToList();
        var versionIds = board.Versions.Values.ToDictionary(v => v.Name, v => v.Id, StringComparer.OrdinalIgnoreCase);
        var takenIds = new HashSet<string>(StringComparer.Ordinal);

        string NewId()
        {
            string id;
            do
                id = change.NewId();
            while (!takenIds.Add(id));
            return id;
        }

        string? Person(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var found = people.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(p.Email, name, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
                return found.Id;
            var person = new BoardPerson { Id = NewId(), Name = name.Trim() };
            people.Add(person);
            preview?.People.Add(person.Name);
            return person.Id;
        }

        string Component(string name)
        {
            var found = components.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
                return found.Id;
            var component = new Component { Id = NewId(), Name = name.Trim() };
            components.Add(component);
            preview?.Components.Add(component.Name);
            return component.Id;
        }

        string Version(string name)
        {
            if (versionIds.TryGetValue(name, out var id))
                return id;
            id = NewId();
            versionIds[name] = id;
            preview?.Versions.Add(name);
            change.Write(BoardPaths.Version(id), new FixVersion { Id = id, Name = name.Trim() });
            return id;
        }

        IssueType TypeFor(string name)
        {
            var exact = project.IssueTypes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                                                               || string.Equals(t.Id, name, StringComparison.OrdinalIgnoreCase));
            var normalized = name.Replace("-", "").Replace(" ", "").ToLowerInvariant();
            var level = normalized.Contains("epic") ? IssueTypeLevel.Epic : normalized.Contains("subtask") ? IssueTypeLevel.Subtask : IssueTypeLevel.Standard;
            var type = exact
                       ?? project.IssueTypes.FirstOrDefault(t => normalized.Contains("bug") && t.Icon == "bug")
                       ?? project.IssueTypes.FirstOrDefault(t => normalized.Contains("story") && t.Icon == "story")
                       ?? project.IssueTypes.FirstOrDefault(t => t.Level == level && (t.Id == "task" || level != IssueTypeLevel.Standard))
                       ?? project.IssueTypes.First(t => t.Level == IssueTypeLevel.Standard);
            if (preview is not null && !string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase))
                preview.Types[name] = type.Name;
            return type;
        }

        string StatusFor(string? name, Workflow workflow)
        {
            var statuses = workflow.Statuses.Select(project.Status).OfType<Status>().ToList();
            if (name is null)
                return workflow.InitialStatus!;
            var exact = statuses.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact.Id;
            // A status sharing a word with it ("Code Review" → "In Review"), then one of the same kind.
            var words = name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4).ToList();
            var similar = statuses.FirstOrDefault(s => words.Any(w => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase)));
            if (similar is not null)
            {
                preview?.Statuses.TryAdd(name, similar.Name);
                return similar.Id;
            }
            var n = name.ToLowerInvariant();
            var category = n.Contains("done") || n.Contains("closed") || n.Contains("resolved") || n.Contains("fixed") || n.Contains("complete")
                ? StatusCategory.Done
                : n.Contains("progress") || n.Contains("review") || n.Contains("test") || n.Contains("doing") ? StatusCategory.InProgress
                : StatusCategory.ToDo;
            var mapped = statuses.FirstOrDefault(s => s.Category == category) ?? statuses[0];
            preview?.Statuses.TryAdd(name, mapped.Name);
            return mapped.Id;
        }


        // A resolution by name, else one sharing a word with it ("Won't Fix" → "Won't Do"), else the first.
        string? ResolutionFor(string? name)
        {
            if (name is null)
                return project.Resolutions.FirstOrDefault()?.Id;
            var exact = project.Resolutions.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact.Id;
            var words = name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4).ToList();
            return (project.Resolutions.FirstOrDefault(r => words.Any(w => r.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    ?? project.Resolutions.FirstOrDefault())?.Id;
        }
        // Pass 1: create every issue (keys in order, ranked last).
        var number = project.NextNumber;
        var rank = CreateIssue.MaxRank(board);
        var created = new List<(ImportedIssue Source, Issue Issue)>();
        var byExternal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in Issues)
        {
            if (string.IsNullOrWhiteSpace(source.Summary))
                continue;
            var type = TypeFor(source.Type);
            var workflow = board.WorkflowFor(type.Id) ?? throw new BoardRuleException(L.T("The board has no workflow."));
            var status = StatusFor(source.Status, workflow);
            var done = project.Status(status)?.Category == StatusCategory.Done;
            rank = Rank.Between(rank, null);
            var resolution = done
                ? ResolutionFor(source.Resolution)
                  ?? project.Resolutions.FirstOrDefault()?.Id
                : null;
            var issue = new Issue
            {
                Id = NewId(),
                Key = IssueKey.Format(project.Key, number++),
                OldKeys = source.ExternalKey is { Length: > 0 } ext && !ext.StartsWith('#') ? [ext.ToUpperInvariant()] : [],
                Type = type.Id,
                Summary = source.Summary.Trim().Length > IssueFields.MaxSummaryLength ? source.Summary.Trim()[..IssueFields.MaxSummaryLength] : source.Summary.Trim(),
                Description = source.Description + (source.ExternalKey is { } k ? L.T($"\n\n_Imported from {Source} ({k})._") : ""),
                Status = status,
                Resolution = resolution,
                Priority = project.Priorities.FirstOrDefault(p => string.Equals(p.Name, source.Priority, StringComparison.OrdinalIgnoreCase))?.Id ?? project.DefaultPriorityId,
                Assignee = Person(source.Assignee),
                Reporter = Person(source.Reporter) ?? project.Person(context.Who)?.Id,
                Labels = source.Labels.Select(x => x.Trim().Replace(' ', '-').Replace(",", "")).Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                Components = source.Components.Select(Component).Distinct().ToList(),
                FixVersions = source.FixVersions.Select(Version).Distinct().ToList(),
                StoryPoints = type.Level == IssueTypeLevel.Subtask ? null : source.StoryPoints,
                Time = new TimeTracking
                {
                    OriginalMinutes = source.OriginalMinutes,
                    RemainingMinutes = source.RemainingMinutes,
                    Log = source.SpentMinutes is { } spent
                        ? [new WorkLog { Id = NewId(), Started = source.Resolved ?? source.Created ?? context.Now, Minutes = spent, Note = L.T($"Imported from {Source}") }]
                        : [],
                },
                Due = source.Due,
                Rank = rank,
                Comments = source.Comments.Where(c => c.Body.Trim().Length > 0)
                    .Select(c => new Comment { Id = NewId(), Author = Person(c.Author), Created = c.Created ?? context.Now, Body = c.Body.Trim() }).ToList(),
            };
            created.Add((source, issue));
            if (source.ExternalKey is { Length: > 0 } external)
                byExternal[external] = issue.Id;
        }

        // Pass 2: parents, where the hierarchy allows (epic → standard → sub-task).
        var levels = created.ToDictionary(c => c.Issue.Id, c => project.IssueType(c.Issue.Type)!.Level);
        for (var i = 0; i < created.Count; i++)
        {
            var (source, issue) = created[i];
            if (source.ParentKey is not { } parentKey || !byExternal.TryGetValue(parentKey, out var parentId))
            {
                if (source.ParentKey is not null)
                    preview?.Warnings.Add(L.T($"Parents outside the import aren't linked (e.g. {source.ParentKey})."));
                continue;
            }
            if (levels[parentId] == levels[issue.Id] + 1)
                created[i] = (source, issue with { Parent = parentId });
            else
                preview?.Warnings.Add(L.T("Some parent links don't fit the epic → story → sub-task hierarchy and are left out."));
        }
        if (created.Any(c => levels[c.Issue.Id] == IssueTypeLevel.Subtask && c.Issue.Parent is null))
        {
            // A sub-task needs a parent: those without one become standard issues.
            var standard = project.IssueTypes.First(t => t.Level == IssueTypeLevel.Standard).Id;
            for (var i = 0; i < created.Count; i++)
                if (levels[created[i].Issue.Id] == IssueTypeLevel.Subtask && created[i].Issue.Parent is null)
                    created[i] = (created[i].Source, created[i].Issue with { Type = standard });
            preview?.Warnings.Add(L.T("Sub-tasks without their parent in the import become standard issues."));
        }

        foreach (var (source, issue) in created)
        {
            change.WriteIssue(null, issue, keepTimes: true);
            var final = change.Issue(issue.Id)!;
            var times = final with
            {
                Created = source.Created ?? final.Created,
                Resolved = final.Resolution is null ? null : source.Resolved ?? source.Created ?? context.Now,
            };
            change.WriteIssue(final, times, keepTimes: true);
        }
        change.WriteProject(project with { NextNumber = number, People = people, Components = components });
        return change.Build(L.T($"{created.Count} issues imported from {Source}"));
    }
}
