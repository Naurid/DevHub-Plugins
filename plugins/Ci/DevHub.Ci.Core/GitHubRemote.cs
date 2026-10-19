namespace DevHub.Ci.Core;

/// <summary>
/// The GitHub repository behind a local one, read from its <c>.git/config</c> (the "origin" remote), without running
/// git: the CI tracker asks for every open repository tab, so no process per tab.
/// </summary>
public static class GitHubRemote
{
    /// <summary>"owner/name" of a github.com remote URL (https or ssh), or null.</summary>
    public static string? Parse(string url)
    {
        var u = url.Trim();
        foreach (var prefix in new[] { "https://github.com/", "http://github.com/", "git@github.com:", "ssh://git@github.com/" })
        {
            if (!u.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var path = u[prefix.Length..].TrimEnd('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                path = path[..^4];
            return path.Count(c => c == '/') == 1 && path.Split('/').All(p => p.Length > 0) ? path : null;
        }
        return null;
    }

    /// <summary>"owner/name" of the repository at <paramref name="folder"/>'s origin, or null (no origin, not GitHub, unreadable).</summary>
    public static string? Of(string folder)
    {
        try
        {
            return GitDirs(folder)?.Common is { } gitDir && File.Exists(Path.Combine(gitDir, "config"))
                ? FromConfig(File.ReadAllText(Path.Combine(gitDir, "config")))
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The origin's URL in a git config file's text, as "owner/name" when it's GitHub.</summary>
    public static string? FromConfig(string config)
    {
        var inOrigin = false;
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inOrigin = line.Replace(" ", "").Equals("[remote\"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inOrigin)
                continue;
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals("url", StringComparison.OrdinalIgnoreCase))
                return Parse(line[(eq + 1)..].Trim().Trim('"'));
        }
        return null;
    }

    /// <summary>The branch checked out in the repository at <paramref name="folder"/> (from <c>.git/HEAD</c>), or null when detached.</summary>
    public static string? CurrentBranch(string folder)
    {
        try
        {
            if (GitDirs(folder)?.Own is not { } gitDir || !File.Exists(Path.Combine(gitDir, "HEAD")))
                return null;
            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            const string prefix = "ref: refs/heads/";
            return head.StartsWith(prefix, StringComparison.Ordinal) ? head[prefix.Length..] : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The .git folder, or where a ".git" file points (a worktree or a submodule: "gitdir: …"): Own holds HEAD; Common
    // holds the config (a worktree's lives in the main repository's folder, which "commondir" names).
    private static (string Own, string Common)? GitDirs(string folder)
    {
        var dotGit = Path.Combine(folder, ".git");
        if (Directory.Exists(dotGit))
            return (dotGit, dotGit);
        if (!File.Exists(dotGit))
            return null;
        var text = File.ReadAllText(dotGit).Trim();
        if (!text.StartsWith("gitdir:", StringComparison.Ordinal))
            return null;
        var own = Path.GetFullPath(Path.Combine(folder, text["gitdir:".Length..].Trim()));
        var common = Path.Combine(own, "commondir");
        return (own, File.Exists(common) ? Path.GetFullPath(Path.Combine(own, File.ReadAllText(common).Trim())) : own);
    }
}
