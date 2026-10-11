namespace DevHub.Git.Core;

/// <summary>A remote: a named URL this repository fetches from and pushes to ("origin").</summary>
/// <param name="PushUrl">Usually the same as <paramref name="FetchUrl"/>; differs only when set up that way.</param>
public sealed record GitRemote(string Name, string FetchUrl, string PushUrl)
{
    public override string ToString() => $"{Name} ({FetchUrl})";
}

// G9: managing remotes. Only ever called from a user action.
public sealed partial class GitRepositoryClient
{
    /// <summary>The remotes with their URLs, in git's order.</summary>
    public async Task<IReadOnlyList<GitRemote>> GetRemoteDetailsAsync(CancellationToken ct = default)
    {
        var result = await Runner.RunAsync(RepositoryPath, ["remote", "-v"], new GitRunOptions { ReadOnly = true }, ct);
        return ParseRemotes(result.StandardOutput);
    }

    /// <summary>
    /// Parses <c>git remote -v</c>: "origin\thttps://… (fetch)" and "origin\thttps://… (push)" per remote.
    /// Names can't contain tabs or spaces, so the tab and the last " (" are safe separators.
    /// </summary>
    public static IReadOnlyList<GitRemote> ParseRemotes(string output)
    {
        var fetch = new Dictionary<string, string>(StringComparer.Ordinal);
        var push = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            var kind = line.LastIndexOf(" (", StringComparison.Ordinal);
            if (tab <= 0 || kind <= tab)
                continue;
            var name = line[..tab];
            var url = line[(tab + 1)..kind];
            if (!order.Contains(name))
                order.Add(name);
            (line.EndsWith("(push)", StringComparison.Ordinal) ? push : fetch)[name] = url;
        }
        return order
            .Select(n => new GitRemote(n, fetch.GetValueOrDefault(n) ?? push.GetValueOrDefault(n, ""),
                push.GetValueOrDefault(n) ?? fetch.GetValueOrDefault(n, "")))
            .ToList();
    }

    /// <summary>Adds a remote (nothing is downloaded until a fetch).</summary>
    public async Task AddRemoteAsync(string name, string url, CancellationToken ct = default) =>
        await Runner.RunAsync(RepositoryPath, ["remote", "add", "--", name, url.Trim()], ct: ct);

    /// <summary>Changes a remote's URL (fetch and push).</summary>
    public async Task SetRemoteUrlAsync(string name, string url, CancellationToken ct = default) =>
        await Runner.RunAsync(RepositoryPath, ["remote", "set-url", "--", name, url.Trim()], ct: ct);

    /// <summary>Renames a remote; its remote-tracking branches and branch upstreams follow.</summary>
    public async Task RenameRemoteAsync(string oldName, string newName, CancellationToken ct = default) =>
        await Runner.RunAsync(RepositoryPath, ["remote", "rename", "--", oldName, newName], ct: ct);

    /// <summary>
    /// Removes a remote here: its remote-tracking branches go and branches stop tracking it.
    /// Nothing changes on the server.
    /// </summary>
    public async Task RemoveRemoteAsync(string name, CancellationToken ct = default) =>
        await Runner.RunAsync(RepositoryPath, ["remote", "remove", "--", name], ct: ct);

    /// <summary>A git setting as this repository sees it (local, then global, then system), or null when unset.</summary>
    public async Task<string?> GetConfigAsync(string key, CancellationToken ct = default)
    {
        var result = await Runner.RunAsync(RepositoryPath, ["config", "--get", key],
            new GitRunOptions { ReadOnly = true, AllowFailure = true }, ct);
        return result.Succeeded && result.StandardOutput.Trim() is { Length: > 0 } value ? value : null;
    }

    /// <summary>Whether <paramref name="name"/> is usable as a remote name (git's ref rules; no spaces or leading "-").</summary>
    public async Task<bool> IsValidRemoteNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-') || name.Any(char.IsWhiteSpace) || name.Contains('/'))
            return false;
        var result = await Runner.RunAsync(RepositoryPath, ["check-ref-format", $"refs/remotes/{name}/x"],
            new GitRunOptions { ReadOnly = true, AllowFailure = true }, ct);
        return result.Succeeded;
    }
}

/// <summary>Clones repositories (there's no repository, so no <see cref="GitRepositoryClient"/>, yet).</summary>
public static class GitCloner
{
    /// <summary>
    /// The folder name git would pick for <paramref name="url"/>: the last path part without
    /// ".git" ("https://github.com/me/my-app.git" → "my-app"). Null when there's none.
    /// </summary>
    public static string? FolderNameFor(string url)
    {
        var trimmed = url.Trim().TrimEnd('/', '\\');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        var cut = trimmed.LastIndexOfAny(['/', '\\', ':']);
        var name = trimmed[(cut + 1)..];
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// Clones <paramref name="url"/> into <paramref name="targetFolder"/> (created; it must not
    /// exist yet or be empty). Network: progress + cancellation; failures throw
    /// <see cref="RemoteOperationException"/> with the reason explained. A cancelled or failed
    /// clone leaves no half-cloned folder behind (git removes it).
    /// </summary>
    public static async Task CloneAsync(GitRunner runner, string url, string targetFolder,
        IProgress<GitProgress>? progress = null, CancellationToken ct = default)
    {
        var target = Path.GetFullPath(targetFolder);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new InvalidOperationException($"{target} already exists and isn't empty.");
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("No parent folder.", nameof(targetFolder));
        Directory.CreateDirectory(parent);

        var lines = progress is null
            ? null
            : new LineProgress(line =>
            {
                if (ProgressParser.Parse(line) is { } p)
                    progress.Report(p);
            });
        try
        {
            await runner.RunAsync(parent, ["clone", "--progress", "--", url.Trim(), target],
                new GitRunOptions { StandardErrorLines = lines }, ct);
        }
        catch (GitException ex)
        {
            throw new RemoteOperationException(RemoteErrorExplainer.Explain(ex.StandardError), ex);
        }
        catch (OperationCanceledException)
        {
            // git was killed mid-clone: don't leave a broken repository behind.
            TryDelete(target);
            throw;
        }
    }

    /// <summary>Calls back synchronously on git's reader thread (Progress&lt;T&gt; would post to the UI thread, out of order).</summary>
    private sealed class LineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort (a file may still be locked for a moment).
        }
    }
}
