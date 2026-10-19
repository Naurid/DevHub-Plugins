using System.Globalization;
using System.Text.Json;

namespace DevHub.Ci.Core;

/// <summary>Where a run, a job or a step is: GitHub's status and conclusion, as one value.</summary>
public enum RunState
{
    Queued,
    Running,
    Passed,
    Failed,
    Cancelled,
    Skipped,
    Other,
}

public static class RunStates
{
    /// <summary>GitHub's <c>status</c> and <c>conclusion</c> as a <see cref="RunState"/>.</summary>
    public static RunState Of(string? status, string? conclusion) => status switch
    {
        "completed" => conclusion switch
        {
            "success" => RunState.Passed,
            "failure" or "timed_out" or "startup_failure" => RunState.Failed,
            "cancelled" => RunState.Cancelled,
            "skipped" or "neutral" => RunState.Skipped,
            _ => RunState.Other,
        },
        "in_progress" => RunState.Running,
        "queued" or "waiting" or "requested" or "pending" => RunState.Queued,
        _ => RunState.Other,
    };

    /// <summary>Not finished yet: worth watching closely.</summary>
    public static bool IsActive(this RunState state) => state is RunState.Queued or RunState.Running;
}

/// <summary>A workflow run (GET repos/{owner}/{repo}/actions/runs).</summary>
/// <param name="Title">What GitHub shows for it: usually the commit message's first line.</param>
/// <param name="Actor">Who started it (login).</param>
public sealed record WorkflowRun(long Id, string Workflow, int Number, int Attempt, string Branch, string HeadSha, string Title,
    string Event, string Actor, RunState State, DateTimeOffset Created, DateTimeOffset Updated, DateTimeOffset? Started, string HtmlUrl)
{
    /// <summary>How long it ran (or has been running, with <paramref name="now"/>), or null before it started.</summary>
    public TimeSpan? Duration(DateTimeOffset now)
    {
        var from = Started ?? Created;
        var to = State.IsActive() ? now : Updated;
        return to > from ? to - from : null;
    }
}

/// <summary>A job of a run (GET …/runs/{id}/jobs) with its steps.</summary>
public sealed record WorkflowJob(long Id, string Name, RunState State, DateTimeOffset? Started, DateTimeOffset? Completed,
    string HtmlUrl, IReadOnlyList<JobStep> Steps)
{
    public TimeSpan? Duration(DateTimeOffset now) =>
        Started is { } s ? (Completed ?? now) - s : null;

    /// <summary>The steps that failed (usually one).</summary>
    public IReadOnlyList<JobStep> FailedSteps => Steps.Where(s => s.State == RunState.Failed).ToList();
}

public sealed record JobStep(int Number, string Name, RunState State);

/// <summary>A page of runs and how many there are in all.</summary>
public sealed record RunPage(IReadOnlyList<WorkflowRun> Runs, int TotalCount);

/// <summary>Reads GitHub's Actions JSON. Fields DevHub doesn't use are ignored; a missing optional one is a default.</summary>
public static class ActionsJson
{
    public static RunPage ParseRuns(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var runs = new List<WorkflowRun>();
        if (root.TryGetProperty("workflow_runs", out var items))
        {
            foreach (var r in items.EnumerateArray())
            {
                runs.Add(new WorkflowRun(
                    r.GetProperty("id").GetInt64(),
                    Str(r, "name"),
                    Int(r, "run_number"),
                    Int(r, "run_attempt", 1),
                    Str(r, "head_branch"),
                    Str(r, "head_sha"),
                    FirstLine(Str(r, "display_title")),
                    Str(r, "event"),
                    r.TryGetProperty("triggering_actor", out var actor) && actor.ValueKind == JsonValueKind.Object ? Str(actor, "login")
                        : r.TryGetProperty("actor", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "login") : "",
                    RunStates.Of(Opt(r, "status"), Opt(r, "conclusion")),
                    Date(r, "created_at") ?? default,
                    Date(r, "updated_at") ?? default,
                    Date(r, "run_started_at"),
                    Str(r, "html_url")));
            }
        }
        return new RunPage(runs, root.TryGetProperty("total_count", out var total) && total.ValueKind == JsonValueKind.Number ? total.GetInt32() : runs.Count);
    }

    public static IReadOnlyList<WorkflowJob> ParseJobs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var jobs = new List<WorkflowJob>();
        if (!doc.RootElement.TryGetProperty("jobs", out var items))
            return jobs;
        foreach (var j in items.EnumerateArray())
        {
            var steps = new List<JobStep>();
            if (j.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array)
                foreach (var step in s.EnumerateArray())
                    steps.Add(new JobStep(Int(step, "number"), Str(step, "name"), RunStates.Of(Opt(step, "status"), Opt(step, "conclusion"))));
            jobs.Add(new WorkflowJob(j.GetProperty("id").GetInt64(), Str(j, "name"),
                RunStates.Of(Opt(j, "status"), Opt(j, "conclusion")), Date(j, "started_at"), Date(j, "completed_at"),
                Str(j, "html_url"), steps));
        }
        return jobs;
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private static string? Opt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Str(JsonElement e, string name) => Opt(e, name) ?? "";

    private static int Int(JsonElement e, string name, int fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Opt(e, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
