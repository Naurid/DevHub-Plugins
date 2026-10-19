namespace DevHub.Ci.Core;

/// <summary>
/// How soon to ask GitHub again about one repository: often while something runs or just after a push, rarely when
/// nothing does, and slower when the hourly rate limit gets low. A 304 (nothing changed, the ETag matched) costs no
/// rate limit, so a quiet repository costs nothing but the request.
/// </summary>
public static class PollSchedule
{
    public static readonly TimeSpan WhileRunning = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan AfterPush = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan LowOnRateLimit = TimeSpan.FromMinutes(5);

    /// <summary>For this long after a push, the new run is expected: ask every <see cref="AfterPush"/>.</summary>
    public static readonly TimeSpan PushWindow = TimeSpan.FromMinutes(1);

    /// <summary>Requests left this hour under which DevHub slows down (other tools share the limit).</summary>
    public const int RateLimitReserve = 200;

    /// <param name="anyActive">A run of the repository is queued or running.</param>
    /// <param name="lastPush">When the user last pushed to the repository from DevHub, or null.</param>
    /// <param name="rateLimitRemaining">What GitHub said is left this hour, or null.</param>
    public static TimeSpan Next(bool anyActive, DateTimeOffset? lastPush, DateTimeOffset now, int? rateLimitRemaining)
    {
        var wait = lastPush is { } push && now - push < PushWindow ? AfterPush
            : anyActive ? WhileRunning
            : Idle;
        return rateLimitRemaining is { } left && left < RateLimitReserve && wait < LowOnRateLimit ? LowOnRateLimit : wait;
    }
}
