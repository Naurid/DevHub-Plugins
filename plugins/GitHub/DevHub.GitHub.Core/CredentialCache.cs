namespace DevHub.GitHub.Core;

/// <summary>
/// The signed-in accounts and their tokens, kept in memory for a few minutes so a burst of API calls (avatars, a
/// board's pull requests, CI polling) doesn't start two Git Credential Manager processes per call. Never written
/// anywhere. <see cref="Forget"/> on sign-in / sign-out, <see cref="ForgetToken"/> when GitHub refuses a token (401).
/// Calls made at the same time share one lookup.
/// </summary>
public sealed class CredentialCache(
    Func<CancellationToken, Task<IReadOnlyList<string>>> listAccounts,
    Func<string, CancellationToken, Task<string?>> borrowToken,
    TimeProvider? time = null,
    TimeSpan? lifetime = null)
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _lifetime = lifetime ?? DefaultLifetime;
    private readonly Lock _gate = new();
    private (Task<IReadOnlyList<string>> Task, DateTimeOffset Until)? _accounts;
    private readonly Dictionary<string, (Task<string?> Task, DateTimeOffset Until)> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<string>> AccountsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_accounts is { } cached && Fresh(cached.Until, cached.Task))
                return cached.Task;
            var task = listAccounts(ct);
            _accounts = (task, _time.GetUtcNow() + _lifetime);
            return task;
        }
    }

    public Task<string?> TokenAsync(string account, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_tokens.TryGetValue(account, out var cached) && Fresh(cached.Until, cached.Task))
                return cached.Task;
            var task = borrowToken(account, ct);
            _tokens[account] = (task, _time.GetUtcNow() + _lifetime);
            return task;
        }
    }

    /// <summary>GitHub refused the token: the next call borrows it again.</summary>
    public void ForgetToken(string account)
    {
        lock (_gate)
            _tokens.Remove(account);
    }

    /// <summary>Signed in or out: everything is looked up again.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _accounts = null;
            _tokens.Clear();
        }
    }

    // A lookup that failed, was cancelled or found nothing isn't kept: the next call tries again.
    private bool Fresh<T>(DateTimeOffset until, Task<T> task) =>
        _time.GetUtcNow() < until && !task.IsFaulted && !task.IsCanceled
        && !(task.IsCompletedSuccessfully && task.Result is null);
}
