namespace DevHub.Ci.Core;

/// <summary>A watched run finished (passed, failed, was cancelled…).</summary>
public sealed record RunFinished(WorkflowRun Run);

/// <summary>
/// Tells which runs of a repository just finished, from one poll to the next: a run seen queued or running that is now
/// done, or a run created after DevHub started that is already done. Runs finished before DevHub started (or before
/// the repository was first polled) are history, not news: no flood of old results when DevHub opens.
/// </summary>
public sealed class RunWatch(DateTimeOffset since)
{
    // The last known state of each run (by id and attempt: a re-run is news again).
    private readonly Dictionary<(long Id, int Attempt), RunState> _seen = new();

    /// <summary>The runs of this poll that finished since the last one (oldest first).</summary>
    public IReadOnlyList<RunFinished> Update(IReadOnlyList<WorkflowRun> runs)
    {
        var finished = new List<RunFinished>();
        foreach (var run in runs.OrderBy(r => r.Updated))
        {
            var key = (run.Id, run.Attempt);
            var known = _seen.TryGetValue(key, out var before);
            _seen[key] = run.State;
            if (run.State.IsActive())
                continue;
            var wasRunning = known && before.IsActive();
            var newSinceStart = !known && run.Created >= since;
            if (wasRunning || newSinceStart)
                finished.Add(new RunFinished(run));
        }
        return finished;
    }
}
