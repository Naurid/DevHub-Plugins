using DevHub.Ci.Core;
using Xunit;

namespace DevHub.Ci.Core.Tests;

public class CiCoreTests
{
    // Trimmed from GET repos/naurid/devhub/actions/runs (fields DevHub doesn't read left out, a few kept to be ignored).
    private const string Runs = """
        {
          "total_count": 128,
          "workflow_runs": [
            {
              "id": 11223344, "name": "CI", "run_number": 57, "run_attempt": 2, "head_branch": "main",
              "head_sha": "2ecc679aa", "display_title": "Merge branch 'Fix/click-away'\n\nmore lines", "event": "push",
              "status": "completed", "conclusion": "failure", "created_at": "2026-10-18T20:00:00Z",
              "updated_at": "2026-10-18T20:06:30Z", "run_started_at": "2026-10-18T20:01:00Z",
              "html_url": "https://github.com/naurid/devhub/actions/runs/11223344",
              "actor": { "login": "someone" }, "triggering_actor": { "login": "naurid" }, "repository": { "id": 1 }
            },
            {
              "id": 11223345, "name": "Release", "run_number": 12, "head_branch": "main", "head_sha": "2ecc679aa",
              "display_title": "Release", "event": "push", "status": "in_progress", "conclusion": null,
              "created_at": "2026-10-18T20:00:05Z", "updated_at": "2026-10-18T20:02:00Z",
              "html_url": "https://github.com/naurid/devhub/actions/runs/11223345"
            }
          ]
        }
        """;

    private const string Jobs = """
        {
          "total_count": 2,
          "jobs": [
            { "id": 901, "name": "build (windows-latest)", "status": "completed", "conclusion": "failure",
              "started_at": "2026-10-18T20:01:10Z", "completed_at": "2026-10-18T20:06:20Z",
              "html_url": "https://github.com/naurid/devhub/actions/runs/11223344/job/901",
              "steps": [
                { "name": "Set up job", "status": "completed", "conclusion": "success", "number": 1 },
                { "name": "Test", "status": "completed", "conclusion": "failure", "number": 4 },
                { "name": "Post checkout", "status": "completed", "conclusion": "skipped", "number": 5 }
              ] },
            { "id": 902, "name": "codeql", "status": "queued", "conclusion": null, "html_url": "", "steps": [] }
          ]
        }
        """;

    [Fact]
    public void Runs_are_read_with_their_state_and_who_started_them()
    {
        var page = ActionsJson.ParseRuns(Runs);

        Assert.Equal(128, page.TotalCount);
        var (failed, running) = (page.Runs[0], page.Runs[1]);
        Assert.Equal((11223344L, "CI", 57, 2, "main"), (failed.Id, failed.Workflow, failed.Number, failed.Attempt, failed.Branch));
        Assert.Equal(RunState.Failed, failed.State);
        Assert.Equal("Merge branch 'Fix/click-away'", failed.Title);   // the first line only
        Assert.Equal("naurid", failed.Actor);                           // who re-ran it, not who pushed first
        Assert.Equal(TimeSpan.FromSeconds(330), failed.Duration(DateTimeOffset.MaxValue));
        Assert.Equal(RunState.Running, running.State);
        Assert.Equal(1, running.Attempt);
        Assert.Equal("", running.Actor);
    }

    [Fact]
    public void Jobs_are_read_with_their_failed_steps()
    {
        var jobs = ActionsJson.ParseJobs(Jobs);

        Assert.Equal(RunState.Failed, jobs[0].State);
        Assert.Equal(["Test"], jobs[0].FailedSteps.Select(s => s.Name));
        Assert.Equal(RunState.Skipped, jobs[0].Steps[2].State);
        Assert.Equal(RunState.Queued, jobs[1].State);
        Assert.Null(jobs[1].Duration(DateTimeOffset.Now));
    }

    [Theory]
    [InlineData("completed", "success", RunState.Passed)]
    [InlineData("completed", "timed_out", RunState.Failed)]
    [InlineData("completed", "startup_failure", RunState.Failed)]
    [InlineData("completed", "cancelled", RunState.Cancelled)]
    [InlineData("completed", "neutral", RunState.Skipped)]
    [InlineData("completed", "action_required", RunState.Other)]
    [InlineData("waiting", null, RunState.Queued)]
    [InlineData("in_progress", null, RunState.Running)]
    public void GitHub_status_and_conclusion_become_one_state(string status, string? conclusion, RunState state) =>
        Assert.Equal(state, RunStates.Of(status, conclusion));

    [Theory]
    [InlineData("https://github.com/Naurid/DevHub.git", "Naurid/DevHub")]
    [InlineData("git@github.com:Naurid/DevHub-Plugins.git", "Naurid/DevHub-Plugins")]
    [InlineData("ssh://git@github.com/naurid/devhub", "naurid/devhub")]
    [InlineData("https://gitlab.com/naurid/devhub.git", null)]
    [InlineData("https://github.com/naurid", null)]
    public void GitHub_remotes_are_recognised(string url, string? slug) => Assert.Equal(slug, GitHubRemote.Parse(url));

    [Fact]
    public void The_origin_is_read_from_the_git_config()
    {
        const string config = """
            [core]
            	bare = false
            [remote "upstream"]
            	url = https://github.com/other/fork.git
            [remote "origin"]
            	url = https://github.com/Naurid/DevHub.git
            	fetch = +refs/heads/*:refs/remotes/origin/*
            """;
        Assert.Equal("Naurid/DevHub", GitHubRemote.FromConfig(config));
        Assert.Null(GitHubRemote.FromConfig("[core]\n\tbare = false\n"));
    }

    [Fact]
    public void The_branch_and_origin_of_a_folder_need_no_git_process()
    {
        var root = Directory.CreateTempSubdirectory("devhub-ci-");
        try
        {
            var git = Directory.CreateDirectory(Path.Combine(root.FullName, ".git"));
            File.WriteAllText(Path.Combine(git.FullName, "HEAD"), "ref: refs/heads/feature/DEV-12-login\n");
            File.WriteAllText(Path.Combine(git.FullName, "config"), "[remote \"origin\"]\n\turl = git@github.com:naurid/devhub.git\n");

            Assert.Equal("feature/DEV-12-login", GitHubRemote.CurrentBranch(root.FullName));
            Assert.Equal("naurid/devhub", GitHubRemote.Of(root.FullName));
            Assert.Null(GitHubRemote.Of(Path.Combine(root.FullName, "missing")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Polling_is_quick_after_a_push_and_while_running_and_slow_otherwise()
    {
        var now = DateTimeOffset.Now;
        Assert.Equal(PollSchedule.AfterPush, PollSchedule.Next(false, now.AddSeconds(-20), now, 5000));
        Assert.Equal(PollSchedule.WhileRunning, PollSchedule.Next(true, now.AddMinutes(-5), now, 5000));
        Assert.Equal(PollSchedule.Idle, PollSchedule.Next(false, null, now, null));
        Assert.Equal(PollSchedule.LowOnRateLimit, PollSchedule.Next(true, null, now, 50));
    }

    [Fact]
    public void Only_runs_that_finish_while_DevHub_watches_are_news()
    {
        var start = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
        var watch = new RunWatch(start);
        WorkflowRun Run(long id, RunState state, int minutes, int attempt = 1) =>
            new(id, "CI", (int)id, attempt, "main", "sha", "t", "push", "me", state, start.AddMinutes(minutes), start.AddMinutes(minutes + 1), null, "");

        // The first poll: an old finished run is history; a running one is watched.
        Assert.Empty(watch.Update([Run(1, RunState.Passed, -60), Run(2, RunState.Running, -1)]));
        // It finishes: news. A new run that started and finished between two polls: news too.
        Assert.Equal([2L, 3L], watch.Update([Run(1, RunState.Passed, -60), Run(2, RunState.Failed, -1), Run(3, RunState.Passed, 2)])
            .Select(f => f.Run.Id));
        // Nothing changed: no news. A re-run of 2 (attempt 2) finishing: news again.
        Assert.Empty(watch.Update([Run(2, RunState.Failed, -1), Run(3, RunState.Passed, 2)]));
        Assert.Empty(watch.Update([Run(2, RunState.Queued, -1, attempt: 2)]));
        Assert.Single(watch.Update([Run(2, RunState.Passed, -1, attempt: 2)]));
    }

    [Fact]
    public void A_failed_log_keeps_the_steps_with_errors_without_timestamps()
    {
        const string log = """
            2026-10-18T20:01:10.1234567Z ##[group]Run actions/checkout@v4
            2026-10-18T20:01:10.2Z with:
            2026-10-18T20:01:10.3Z ##[endgroup]
            2026-10-18T20:01:11.0Z Checked out.
            2026-10-18T20:02:00.0Z ##[group]Run dotnet test -c Release
            2026-10-18T20:02:00.1Z ##[endgroup]
            2026-10-18T20:05:00.0Z   Passed!  - Failed: 0
            2026-10-18T20:06:00.0Z ##[error]Process completed with exit code 1.
            2026-10-18T20:06:01.0Z ##[group]Run actions/upload-artifact@v4
            2026-10-18T20:06:02.0Z ##[endgroup]
            """;

        var lines = LogExcerpt.Failed(log);

        Assert.Equal("Run dotnet test -c Release", lines[0].Text);
        Assert.True(lines[0].IsHeader);
        Assert.Contains(lines, l => l is { IsError: true, Text: "Process completed with exit code 1." });
        Assert.DoesNotContain(lines, l => l.Text.Contains("checkout") || l.Text.Contains("upload-artifact"));
        Assert.DoesNotContain(lines, l => l.Text.StartsWith("2026-", StringComparison.Ordinal));
    }

    [Fact]
    public void A_log_without_error_markers_gives_its_end()
    {
        var log = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"2026-10-18T20:00:00Z line {i}"));
        var lines = LogExcerpt.Failed(log);
        Assert.Equal(200, lines.Count);
        Assert.Equal("line 500", lines[^1].Text);
    }
}
