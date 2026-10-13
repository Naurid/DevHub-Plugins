using System.Text.Json;
using DevHub.Board.Core.Fields;
using DevHub.Board.Core.Import;
using DevHub.Board.Core.Model;
using Xunit;

namespace DevHub.Board.Core.Tests;

public class CsvTests
{
    [Fact]
    public void Reads_quotes_commas_and_line_breaks()
    {
        var rows = Csv.Read("﻿a,b,c\r\n1,\"two, too\",\"line\nbreak \"\"quoted\"\"\"\n\n4,,6\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal(["1", "two, too", "line\nbreak \"quoted\""], rows[1]);
        Assert.Equal(["4", "", "6"], rows[2]);
    }

    [Fact]
    public void Writing_and_reading_round_trips()
    {
        string?[] row = ["plain", "with, comma", "with \"quote\"", "multi\nline", null];
        var back = Csv.Read(Csv.Write([row]));

        Assert.Equal(["plain", "with, comma", "with \"quote\"", "multi\nline", ""], back[0]);
    }
}

public class JiraImportTests
{
    private const string JiraCsv =
        "Summary,Issue key,Issue id,Parent id,Issue Type,Status,Priority,Assignee,Reporter,Created,Resolved,Labels,Labels,Component/s,Fix Version/s,Custom field (Story Points),Original Estimate,Time Spent,Description,Comment\n" +
        "Accounts,PROJ-1,10001,,Epic,In Progress,High,Grace Hopper,Ada Lovelace,01/Oct/26 9:00 AM,,auth,,,1.0,,,,The epic,\n" +
        "Login form,PROJ-2,10002,10001,Story,Done,Medium,Grace Hopper,Ada Lovelace,02/Oct/26 10:30 AM,05/Oct/26 4:00 PM,auth,ui,Frontend,1.0,5,28800,14400,\"Has, commas\",\"03/Oct/26 11:00 AM;Grace Hopper;Started\"\n" +
        "Layout,PROJ-3,10003,10002,Sub-task,Code Review,Low,,Ada Lovelace,02/Oct/26 11:00 AM,,,,,,,,,,\n";

    [Fact]
    public void Reads_jiras_columns()
    {
        var issues = JiraCsvImport.Read(JiraCsv);

        Assert.Equal(3, issues.Count);
        var login = issues[1];
        Assert.Equal(("PROJ-2", "Story", "Done", "PROJ-1"), (login.ExternalKey, login.Type, login.Status, login.ParentKey));
        Assert.Equal(["auth", "ui"], login.Labels);
        Assert.Equal((5.0, 480, 240), (login.StoryPoints!.Value, login.OriginalMinutes!.Value, login.SpentMinutes!.Value));
        Assert.Equal("Has, commas", login.Description);
        Assert.Equal(("Grace Hopper", "Started"), (login.Comments[0].Author, login.Comments[0].Body));
        Assert.Equal(new DateTime(2026, 10, 2, 10, 30, 0), login.Created!.Value.LocalDateTime);
        Assert.Equal("PROJ-2", issues[2].ParentKey);
    }

    [Fact]
    public void Imports_onto_the_board_with_names_mapped_and_parents_linked()
    {
        using var board = TestBoard.Create();
        board.Create("story", "Existing");
        var import = new ImportIssues(JiraCsvImport.Read(JiraCsv), "Jira");

        var preview = import.Preview(board.Snapshot, new OperationContext(board.Session.CurrentPersonId, board.Time.Now));
        Assert.Equal(3, preview.Issues);
        Assert.Contains("Grace Hopper", preview.NewPeople);
        Assert.Equal(["Frontend"], preview.NewComponents);
        Assert.Equal(["1.0"], preview.NewVersions);
        Assert.Equal("In Review", preview.Statuses["Code Review"]);

        board.Apply(import);

        var snapshot = board.Snapshot;
        var epic = snapshot.IssueByKey("PROJ-1")!;
        var login = snapshot.IssueByKey("PROJ-2")!;
        var layout = snapshot.IssueByKey("PROJ-3")!;
        Assert.Equal(("DEV-2", "epic", "inprogress", "high"), (epic.Key, epic.Type, epic.Status, epic.Priority));
        Assert.Equal((epic.Id, "done", "done"), (login.Parent, login.Status, login.Resolution));
        Assert.Equal((login.Id, "subtask", "inreview"), (layout.Parent, layout.Type, layout.Status));
        Assert.Equal("Grace Hopper", snapshot.Project.Person(login.Assignee)!.Name);
        Assert.Equal(["Frontend"], login.Components.Select(c => snapshot.Project.Component(c)!.Name));
        Assert.Equal("1.0", snapshot.Versions[login.FixVersions.Single()].Name);
        Assert.Equal((480, 240), (login.Time.OriginalMinutes!.Value, login.Time.SpentMinutes));
        Assert.Equal(new DateTime(2026, 10, 2, 10, 30, 0), login.Created.LocalDateTime);
        Assert.Equal(new DateTime(2026, 10, 5, 16, 0, 0), login.Resolved!.Value.LocalDateTime);
        Assert.Equal("Started", login.Comments.Single().Body);
        Assert.Equal(5, snapshot.Project.NextNumber);
        Assert.Contains("Imported from Jira (PROJ-2)", login.Description);
        Assert.Empty(board.Reload().Problems);
    }

    [Fact]
    public void A_csv_without_a_summary_column_is_refused()
    {
        Assert.Throws<FormatException>(() => JiraCsvImport.Read("Key,Title\nA-1,x\n"));
    }
}

public class GitHubImportTests
{
    [Fact]
    public void Reads_issues_but_not_pull_requests()
    {
        const string page = """
            [
              { "number": 7, "title": "Crash on start", "body": "Steps…", "state": "open", "labels": [{ "name": "bug" }, { "name": "needs triage" }],
                "user": { "login": "ada" }, "assignee": { "login": "grace" }, "created_at": "2026-10-01T09:00:00Z" },
              { "number": 8, "title": "Add login", "body": null, "state": "closed", "state_reason": "completed", "labels": [],
                "user": { "login": "ada" }, "assignee": null, "milestone": { "title": "v1" }, "created_at": "2026-10-02T09:00:00Z", "closed_at": "2026-10-04T09:00:00Z" },
              { "number": 9, "title": "A pull request", "state": "open", "pull_request": {}, "user": { "login": "ada" }, "created_at": "2026-10-02T09:00:00Z" }
            ]
            """;

        var issues = GitHubIssuesImport.Read([page]);

        Assert.Equal(2, issues.Count);
        Assert.Equal(("#7", "Bug", "To Do", "grace"), (issues[0].ExternalKey, issues[0].Type, issues[0].Status, issues[0].Assignee));
        Assert.Equal(["needs triage"], issues[0].Labels);
        Assert.Equal(("Done", "Done", "v1"), (issues[1].Status, issues[1].Resolution, issues[1].FixVersions.Single()));

        using var board = TestBoard.Create();
        board.Apply(new ImportIssues(issues, "GitHub naurid/devhub"));
        Assert.Equal(["needs-triage"], board.Snapshot.Issues.Values.Single(i => i.Summary == "Crash on start").Labels);
    }
}

public class ExportTests
{
    [Fact]
    public void Csv_has_names_and_reads_back()
    {
        using var board = TestBoard.Create();
        var epic = board.Create("epic", "Accounts");
        var story = board.Create("story", "Login, form", parent: epic.Id);
        board.Apply(new EditIssue(story.Id, IssueFields.StoryPoints, new FieldValue.Number(3)));

        var rows = Csv.Read(BoardExport.ToCsv(board.Snapshot));

        Assert.Equal(BoardExport.CsvColumns, rows[0]);
        var row = rows.Single(r => r[0] == story.Key);
        Assert.Equal(("Story", "Login, form", "To Do", epic.Key, "3"), (row[1], row[2], row[3], row[9], row[11]));
    }

    [Fact]
    public void Json_holds_everything()
    {
        using var board = TestBoard.Create();
        var story = board.Create("story", "Login");
        board.Apply(new AddComment(story.Id, "Hello"));

        using var json = JsonDocument.Parse(BoardExport.ToJson(board.Snapshot));

        Assert.Equal("DEV", json.RootElement.GetProperty("project").GetProperty("key").GetString());
        var issue = json.RootElement.GetProperty("issues")[0];
        Assert.Equal("Hello", issue.GetProperty("comments")[0].GetProperty("body").GetString());
        Assert.True(issue.GetProperty("history").GetArrayLength() >= 2);
    }
}
