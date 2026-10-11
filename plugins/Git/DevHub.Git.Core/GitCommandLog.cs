namespace DevHub.Git.Core;

/// <summary>One git command DevHub ran (shown in the Git Console).</summary>
/// <param name="RepositoryPath">Working directory it ran in.</param>
/// <param name="Arguments">Arguments after "git" (without DevHub's own "-c" options).</param>
/// <param name="Started">When it started.</param>
/// <param name="Duration">How long it took.</param>
/// <param name="ExitCode">Process exit code; -1 when it was cancelled.</param>
/// <param name="Output">The first part of stderr (or stdout when stderr is empty), for display.</param>
/// <param name="FailureExpected">
/// The caller ran it with AllowFailure: a non-zero exit is an answer, not an error (e.g.
/// <c>symbolic-ref -q HEAD</c> exits 1 for a detached HEAD). The Git Console doesn't flag those.
/// </param>
public sealed record GitCommandEntry(
    string RepositoryPath,
    IReadOnlyList<string> Arguments,
    DateTimeOffset Started,
    TimeSpan Duration,
    int ExitCode,
    string Output,
    bool FailureExpected = false)
{
    /// <summary>"git status --porcelain=v2 …" as one line.</summary>
    public string CommandLine => "git " + string.Join(' ', Arguments.Select(Quote));

    public bool Succeeded => ExitCode == 0;

    private static string Quote(string arg) =>
        arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg;
}

/// <summary>Every git command DevHub runs, newest last. Rule: nothing runs behind the user's back.</summary>
public interface IGitCommandLog
{
    /// <summary>A snapshot of the entries, oldest first.</summary>
    IReadOnlyList<GitCommandEntry> Entries { get; }

    /// <summary>Raised after an entry is added. May be raised on a background thread.</summary>
    event EventHandler<GitCommandEntry>? EntryAdded;

    void Add(GitCommandEntry entry);

    void Clear();
}

/// <summary>Thread-safe, size-capped <see cref="IGitCommandLog"/> (oldest entries drop off).</summary>
public sealed class GitCommandLog(int capacity = 500) : IGitCommandLog
{
    private readonly Lock _lock = new();
    private readonly LinkedList<GitCommandEntry> _entries = new();

    public IReadOnlyList<GitCommandEntry> Entries
    {
        get
        {
            lock (_lock)
                return _entries.ToList();
        }
    }

    public event EventHandler<GitCommandEntry>? EntryAdded;

    public void Add(GitCommandEntry entry)
    {
        lock (_lock)
        {
            _entries.AddLast(entry);
            while (_entries.Count > capacity)
                _entries.RemoveFirst();
        }

        EntryAdded?.Invoke(this, entry);
    }

    public void Clear()
    {
        lock (_lock)
            _entries.Clear();
    }
}
