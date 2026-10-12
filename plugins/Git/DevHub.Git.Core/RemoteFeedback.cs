using System.Text.RegularExpressions;

namespace DevHub.Git.Core;

/// <summary>A step of a fetch / pull / push as git reports it ("Receiving objects", 45 %).</summary>
/// <param name="Phase">What git is doing, e.g. "Receiving objects", "Counting objects (remote)".</param>
/// <param name="Percent">0–100, or null for steps without a percentage.</param>
public sealed record GitProgress(string Phase, int? Percent);

/// <summary>Turns git's progress lines (stderr with <c>--progress</c>) into <see cref="GitProgress"/>.</summary>
public static partial class ProgressParser
{
    // "Receiving objects:  45% (450/1000), 1.20 MiB | 2.00 MiB/s", "remote: Counting objects: 100% (5/5), done."
    [GeneratedRegex(@"^(?<remote>remote:\s*)?(?<phase>[A-Za-z][A-Za-z ]*?):\s+(?<pct>\d{1,3})%")]
    private static partial Regex PercentLine();

    // "remote: Enumerating objects: 5, done.", "Counting objects: 12"
    [GeneratedRegex(@"^(?<remote>remote:\s*)?(?<phase>[A-Za-z][A-Za-z ]*?):\s+\d+")]
    private static partial Regex CountLine();

    /// <summary>The progress in <paramref name="line"/>, or null when it isn't a progress line.</summary>
    public static GitProgress? Parse(string line)
    {
        var m = PercentLine().Match(line);
        if (m.Success)
            return new GitProgress(Phase(m), Math.Clamp(int.Parse(m.Groups["pct"].Value), 0, 100));
        m = CountLine().Match(line);
        return m.Success ? new GitProgress(Phase(m), null) : null;

        static string Phase(Match m) =>
            m.Groups["remote"].Success ? m.Groups["phase"].Value + " (remote)" : m.Groups["phase"].Value;
    }
}

/// <summary>Why a fetch / pull / push failed, in categories the UI can explain and offer a way out of.</summary>
public enum RemoteErrorKind
{
    Other,

    /// <summary>Wrong or missing credentials, or no access (HTTPS 401/403, SSH key refused).</summary>
    Authentication,

    /// <summary>SSH doesn't know (or distrusts) the server's host key.</summary>
    HostKey,

    /// <summary>No connection: host not found, timeout, proxy, TLS.</summary>
    Network,

    /// <summary>The remote URL points at nothing (wrong URL, or no access to a private repository).</summary>
    RepositoryNotFound,

    /// <summary>Push refused: the remote branch has commits you don't have (pull first).</summary>
    Rejected,

    /// <summary>The branch has no upstream to pull from / push to.</summary>
    NoUpstream,

    /// <summary>Pull with fast-forward only, but local and remote have both moved on.</summary>
    Diverged,

    /// <summary>Pull would overwrite uncommitted local changes.</summary>
    LocalChangesInTheWay,

    /// <summary>A pull (rebase or merge) stopped on conflicts; the repository is mid-rebase / mid-merge.</summary>
    Conflicts,
}

/// <summary>How a pull reconciles local and remote commits.</summary>
public enum PullMode
{
    /// <summary>Only move forward (no merge commit); fail if both sides have new commits.</summary>
    FastForwardOnly,

    /// <summary>Put local commits on top of the remote ones (<c>pull --rebase --autostash</c>).</summary>
    Rebase,
}

/// <summary>A fetch / pull / push failed; <see cref="Error"/> says why in plain words.</summary>
public sealed class RemoteOperationException(RemoteError error, GitException inner)
    : Exception(error.Title + ": " + error.Explanation, inner)
{
    public RemoteError Error { get; } = error;

    /// <summary>git's own error output, for "show details".</summary>
    public string GitOutput => ((GitException)InnerException!).StandardError.Trim();
}

/// <summary>A failed remote operation, explained.</summary>
/// <param name="Title">Short summary ("Authentication failed").</param>
/// <param name="Explanation">What it means and what to do, in plain words.</param>
public sealed record RemoteError(RemoteErrorKind Kind, string Title, string Explanation);

/// <summary>
/// Recognises the common git / ssh / credential-helper failure messages (DevHub runs git with
/// <c>LC_ALL=C</c>, so they're in English) and explains them. Unknown failures keep git's own text.
/// </summary>
public static class RemoteErrorExplainer
{
    public static RemoteError Explain(string stderr)
    {
        var s = stderr;
        bool Has(string text) => s.Contains(text, StringComparison.OrdinalIgnoreCase);

        // Order matters: a rejected push also mentions the URL, etc.
        if (Has("CONFLICT (") || Has("could not apply") || Has("Automatic merge failed") || Has("Resolve all conflicts manually"))
            return new(RemoteErrorKind.Conflicts, "Stopped on conflicts",
                "Some of your changes conflict with the remote ones. The conflicting files are marked in " +
                "Local Changes; resolve them and continue, or abort to go back to how things were " +
                "(\"git rebase --abort\" / \"git merge --abort\" in a terminal for now; DevHub gets buttons for this in G7).");

        if (Has("[rejected]") || Has("non-fast-forward") || Has("fetch first") || Has("stale info"))
            return new(RemoteErrorKind.Rejected, "Push rejected",
                "The remote branch has commits that you don't have yet. Update (pull) first, then push again.");

        if (Has("has no upstream branch") || Has("no tracking information") || Has("There is no tracking information"))
            return new(RemoteErrorKind.NoUpstream, "No upstream branch",
                "This branch isn't connected to a remote branch yet. Push it once to create and track it.");

        if (Has("Not possible to fast-forward") || Has("Diverging branches can't be fast-forwarded")
            || Has("have diverged") || Has("Need to specify how to reconcile divergent branches"))
            return new(RemoteErrorKind.Diverged, "Branches have diverged",
                "Both your branch and the remote branch have new commits, so it can't simply move forward. " +
                "Rebase puts your commits on top of the remote ones.");

        if (Has("would be overwritten by merge") || Has("cannot pull with rebase") || Has("Your local changes to the following files would be overwritten"))
            return new(RemoteErrorKind.LocalChangesInTheWay, "Local changes in the way",
                "Updating would overwrite files you've changed. Commit or stash your changes, then update again.");

        if (Has("Host key verification failed") || Has("REMOTE HOST IDENTIFICATION HAS CHANGED"))
            return new(RemoteErrorKind.HostKey, "Unknown or changed SSH host key",
                "SSH doesn't trust this server yet (or its key changed). Connect once from a terminal " +
                "(for example \"ssh -T git@github.com\") to check and accept the key, then try again.");

        if (Has("Permission denied (publickey") || Has("Authentication failed") || Has("could not read Username")
            || Has("could not read Password") || Has("terminal prompts disabled") || Has("HTTP Basic: Access denied")
            || Has("returned error: 401") || Has("returned error: 403") || Has("The requested URL returned error: 403")
            || Has("Invalid username or password") || Has("Permission to") && Has("denied"))
            return new(RemoteErrorKind.Authentication, "Authentication failed",
                "The remote didn't accept your credentials, or you don't have access. For HTTPS, sign in through " +
                "Git Credential Manager (run a fetch from a terminal once) or use a personal access token; " +
                "for SSH, check that your key is loaded (ssh-agent) and added to your account.");

        if (Has("Repository not found") || Has("does not appear to be a git repository") || Has("not found") && Has("repository"))
            return new(RemoteErrorKind.RepositoryNotFound, "Repository not found",
                "The remote address doesn't point to a repository you can reach. Check the remote URL, " +
                "and that your account has access if it's private.");

        if (Has("Could not resolve host") || Has("Failed to connect") || Has("Connection timed out")
            || Has("Connection refused") || Has("Could not read from remote repository") || Has("unable to access")
            || Has("SSL certificate problem") || Has("Operation timed out") || Has("Network is unreachable"))
            return new(RemoteErrorKind.Network, "Can't reach the remote",
                "The connection to the server failed (no network, the host name is wrong, a proxy or firewall, " +
                "or a certificate problem). Check your connection and try again.");

        var first = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal))
            ?? stderr.Trim();
        return new(RemoteErrorKind.Other, "Git reported an error", first);
    }
}
