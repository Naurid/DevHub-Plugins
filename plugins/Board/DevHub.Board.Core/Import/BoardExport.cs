using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevHub.Board.Core.Model;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Core.Import;

/// <summary>Issues out of a board: a CSV for spreadsheets (names, not ids) and a JSON with everything.</summary>
public static class BoardExport
{
    public static readonly IReadOnlyList<string> CsvColumns =
    [
        L.T("Key"), L.T("Type"), L.T("Summary"), L.T("Status"), L.T("Resolution"), L.T("Priority"), L.T("Assignee"), L.T("Reporter"), L.T("Parent"), L.T("Epic"), L.T("Sprint"),
        L.T("Story points"), L.T("Original estimate (minutes)"), L.T("Remaining estimate (minutes)"), L.T("Time spent (minutes)"),
        L.T("Labels"), L.T("Components"), L.T("Fix versions"), L.T("Start"), L.T("Due"), L.T("Created"), L.T("Updated"), L.T("Resolved"), L.T("Description"),
        L.T("Acceptance criteria"), L.T("Definition of Done"),
    ];

    /// <summary>
    /// The issues (in rank order) as CSV, one row each; lists separated by "; ". After <see cref="CsvColumns"/> comes a
    /// column per custom field. Acceptance criteria are one per line ("[x] …" when met); Definition of Done is "4/6".
    /// </summary>
    public static string ToCsv(BoardSnapshot board, IEnumerable<Issue>? issues = null)
    {
        var project = board.Project;
        string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string? Time(DateTimeOffset? t) => t?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        IReadOnlyList<string?> header = [.. CsvColumns, .. project.CustomFields.Select(f => f.Name)];
        var rows = new List<IReadOnlyList<string?>> { header };
        foreach (var i in (issues ?? board.Issues.Values).OrderBy(i => i.Rank, StringComparer.Ordinal))
        {
            rows.Add(
            [
                i.Key, project.IssueType(i.Type)?.Name, i.Summary, project.Status(i.Status)?.Name, project.Resolution(i.Resolution)?.Name,
                project.Priority(i.Priority)?.Name, project.Person(i.Assignee)?.Name, project.Person(i.Reporter)?.Name,
                board.Issue(i.Parent)?.Key, board.EpicOf(i)?.Key, i.Sprint is null ? null : board.Sprints.GetValueOrDefault(i.Sprint)?.Name,
                i.StoryPoints?.ToString(CultureInfo.InvariantCulture),
                i.Time.OriginalMinutes?.ToString(CultureInfo.InvariantCulture), i.Time.RemainingMinutes?.ToString(CultureInfo.InvariantCulture),
                i.Time.Log.Count == 0 ? null : i.Time.SpentMinutes.ToString(CultureInfo.InvariantCulture),
                string.Join("; ", i.Labels), string.Join("; ", i.Components.Select(c => project.Component(c)?.Name ?? c)),
                string.Join("; ", i.FixVersions.Select(v => board.Versions.GetValueOrDefault(v)?.Name ?? v)),
                Date(i.Start), Date(i.Due), Time(i.Created), Time(i.Updated), Time(i.Resolved), i.Description,
                string.Join("\n", i.Criteria.Select(c => (c.Done ? "[x] " : "[ ] ") + c.Text)),
                project.DefinitionOfDone.Count == 0 || board.LevelOf(i) != IssueTypeLevel.Standard ? null
                    : $"{project.DefinitionOfDone.Count(d => i.DoneChecks.Contains(d.Id))}/{project.DefinitionOfDone.Count}",
                .. project.CustomFields.Select(f => i.Custom.GetValueOrDefault(f.Id)),
            ]);
        }
        return Csv.Write(rows);
    }

    /// <summary>
    /// Everything as one JSON document: the project, its workflows, boards, versions, sprints and issues (with
    /// comments, work logs and change logs). Re-readable by other tools; DevHub's own backup is the board folder.
    /// </summary>
    public static string ToJson(BoardSnapshot board)
    {
        JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, BoardJson.Options);
        var issues = new JsonArray();
        foreach (var issue in board.Issues.Values.OrderBy(i => i.Rank, StringComparer.Ordinal))
        {
            var node = Node(issue)!.AsObject();
            node["history"] = Node(board.HistoryOf(issue.Id));
            issues.Add(node);
        }
        var root = new JsonObject
        {
            ["exportedBy"] = "DevHub Board",
            ["schema"] = BoardJson.SchemaVersion,
            ["project"] = Node(board.Project),
            ["workflows"] = Node(board.Workflows.Values.OrderBy(w => w.Name).ToList()),
            ["boards"] = Node(board.Boards.Values.OrderBy(b => b.Name).ToList()),
            ["versions"] = Node(board.Versions.Values.OrderBy(v => v.Name).ToList()),
            ["sprints"] = Node(board.Sprints.Values.OrderBy(s => s.Sequence).ToList()),
            ["issues"] = issues,
        };
        return root.ToJsonString(BoardJson.Options) + "\n";
    }
}
