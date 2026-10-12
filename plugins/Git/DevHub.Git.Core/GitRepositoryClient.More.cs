namespace DevHub.Git.Core;

// G8: stashes, tags, cherry-pick / revert, blame, submodules. Anything that changes the
// repository is only ever called from a user action.
public sealed partial class GitRepositoryClient
{
    // ---- Stashes ---------------------------------------------------------------------------------

    /// <summary>The stashes, newest first (index 0 = "stash@{0}").</summary>
    public async Task<IReadOnlyList<GitStash>> GetStashesAsync(CancellationToken ct = default)
    {
        // stash list takes git log's options; the log format gives id, parents, date and subject.
        var result = await Runner.RunAsync(RepositoryPath, ["stash", "list", "--no-color", "--format=" + LogParser.Format],
            new GitRunOptions { ReadOnly = true }, ct);
        return LogParser.Parse(result.StandardOutput).Select((c, i) => new GitStash(i, c)).ToList();
    }

    /// <summary>
    /// Puts the uncommitted changes aside (<c>stash push</c>) and returns the working tree to the
    /// last commit. <paramref name="includeUntracked"/> takes new files along too;
    /// <paramref name="keepIndex"/> leaves the staged changes in place as well. Returns false
    /// when there was nothing to stash. Throws <see cref="InvalidOperationException"/> while a file is
    /// in conflict (git refuses then).
    /// </summary>
    public async Task<bool> StashAsync(string? message = null, bool includeUntracked = true, bool keepIndex = false,
        CancellationToken ct = default)
    {
        // git's own answer is "could not write index / needs merge", which doesn't say what to do.
        if ((await GetStatusAsync(ct)).Changes.FirstOrDefault(c => c.IsConflicted) is { } conflict)
            throw new InvalidOperationException(
                $"{conflict.Path} has unresolved conflicts, and git can't stash a conflicted file. Resolve it in Local Changes " +
                "(Accept Yours / Theirs, or fix it and Mark as Resolved) first.");

        var before = await StashTopAsync(ct);
        List<string> args = ["stash", "push"];
        if (includeUntracked)
            args.Add("--include-untracked");
        if (keepIndex)
            args.Add("--keep-index");
        if (!string.IsNullOrWhiteSpace(message))
            args.AddRange(["-m", message.Trim()]);
        await Runner.RunAsync(RepositoryPath, args, ct: ct);
        // "No local changes to save" also exits 0.
        return await StashTopAsync(ct) != before;
    }

    /// <summary>
    /// Puts a stash's changes back into the working tree (<c>stash apply</c>), and with
    /// <paramref name="pop"/> drops it afterwards. When the changes clash with the files as they
    /// are now, the conflicts are left marked in the files and the stash is kept. Throws when git
    /// refuses to start (e.g. local changes to the same files would be overwritten).
    /// </summary>
    public async Task<StashApplyResult> ApplyStashAsync(GitStash stash, bool pop, CancellationToken ct = default)
    {
        string[] args = ["stash", pop ? "pop" : "apply", await StashRefAsync(stash, ct)];
        var result = await Runner.RunAsync(RepositoryPath, args, new GitRunOptions { AllowFailure = true }, ct);
        if (result.Succeeded)
            return StashApplyResult.Applied;
        if ((await GetStatusAsync(ct)).Changes.Any(c => c.IsConflicted))
            return StashApplyResult.Conflicts;
        throw new GitException(args, result.ExitCode, result.StandardError);
    }

    /// <summary>Deletes a stash. Its changes are gone (the caller asks first).</summary>
    public async Task DropStashAsync(GitStash stash, CancellationToken ct = default) =>
        await Runner.RunAsync(RepositoryPath, ["stash", "drop", "-q", await StashRefAsync(stash, ct)], ct: ct);

    /// <summary>
    /// The files in a stash: the tracked changes (compared with the commit it was made on), then
    /// the untracked files it took along, if any (shown as added).
    /// </summary>
    public async Task<IReadOnlyList<CommitFile>> GetStashChangesAsync(GitStash stash, CancellationToken ct = default)
    {
        var files = (await GetCommitChangesAsync(stash.Commit, ct)).Select(c => new CommitFile(c, stash.Commit)).ToList();
        if (stash.HasUntracked)
        {
            // The third parent is a root commit holding just the untracked files.
            var untracked = new GitCommit { Id = stash.Commit.ParentIds[2] };
            files.AddRange((await GetCommitChangesAsync(untracked, ct)).Select(c => new CommitFile(c, untracked)));
        }
        return files;
    }

    /// <summary>
    /// "stash@{n}" for <paramref name="stash"/> as the list is <i>now</i>: indexes shift when
    /// stashes are added or dropped, so the stash is found again by its id.
    /// </summary>
    private async Task<string> StashRefAsync(GitStash stash, CancellationToken ct)
    {
        var now = await GetStashesAsync(ct);
        return now.FirstOrDefault(s => s.Id == stash.Id)?.Ref
               ?? throw new InvalidOperationException("That stash doesn't exist any more (it was applied or dropped elsewhere).");
    }

    // ---- Tags ------------------------------------------------------------------------------------

    /// <summary>All tags, newest first.</summary>
    public async Task<IReadOnlyList<GitTag>> GetTagsAsync(CancellationToken ct = default)
    {
        var result = await Runner.RunAsync(RepositoryPath,
            ["for-each-ref", "--sort=-creatordate", "--format=" + TagParser.Format, "refs/tags"],
            new GitRunOptions { ReadOnly = true }, ct);
        return TagParser.Parse(result.StandardOutput);
    }

    /// <summary>
    /// Tags <paramref name="target"/> (a commit id; null = HEAD). With a <paramref name="message"/>
    /// it's an annotated tag (message, tagger and date of its own; what releases usually use),
    /// without one a lightweight tag (just a name).
    /// </summary>
    public async Task CreateTagAsync(string name, string? target = null, string? message = null, CancellationToken ct = default)
    {
        var annotated = !string.IsNullOrWhiteSpace(message);
        List<string> args = annotated ? ["tag", "-a", "-F", "-", name] : ["tag", name];
        if (target is not null)
            args.Add(target);
        await Runner.RunAsync(RepositoryPath, args,
            annotated ? new GitRunOptions { StandardInput = message!.Replace("\r\n", "\n", StringComparison.Ordinal) } : null, ct);
    }

    /// <summary>Whether <paramref name="name"/> is a valid tag name (git's rules; doesn't check that it's free).</summary>
    public async Task<bool> IsValidTagNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-'))
            return false;
        var result = await Runner.RunAsync(RepositoryPath, ["check-ref-format", "refs/tags/" + name],
            new GitRunOptions { ReadOnly = true, AllowFailure = true }, ct);
        return result.Succeeded;
    }

    /// <summary>Deletes a local tag (a pushed copy on the remote stays; see <see cref="DeleteRemoteTagAsync"/>).</summary>
    public async Task DeleteTagAsync(string name, CancellationToken ct = default) =>
        await Runner.RunAsync(RepositoryPath, ["tag", "-d", name], ct: ct);

    /// <summary>Sends one tag to <paramref name="remote"/> (pushing a branch doesn't send tags).</summary>
    public Task PushTagAsync(string remote, string name, IProgress<GitProgress>? progress = null, CancellationToken ct = default) =>
        RunRemoteAsync(["push", "--progress", remote, "refs/tags/" + name], progress, ct);

    /// <summary>Deletes a tag on <paramref name="remote"/> (the local tag stays).</summary>
    public Task DeleteRemoteTagAsync(string remote, string name, IProgress<GitProgress>? progress = null,
        CancellationToken ct = default) =>
        RunRemoteAsync(["push", "--progress", remote, "--delete", "refs/tags/" + name], progress, ct);

    // ---- Cherry-pick / revert --------------------------------------------------------------------

    /// <summary>
    /// Copies <paramref name="commit"/>'s changes onto the current branch as a new commit (same
    /// message). For a merge commit, the changes it brought in (compared with its first parent).
    /// Conflicts leave the cherry-pick in progress; a commit whose changes are already here gives
    /// <see cref="OperationOutcome.UpToDate"/> and changes nothing.
    /// </summary>
    public Task<OperationOutcome> CherryPickAsync(GitCommit commit, CancellationToken ct = default) =>
        PickAsync([.. NoEditor, "cherry-pick", .. MainlineArgs(commit), commit.Id], RepositoryOperation.CherryPicking, ct);

    /// <summary>
    /// Undoes <paramref name="commit"/> with a new commit that applies the opposite changes
    /// ("Revert "subject""); history isn't rewritten, so it's safe on pushed commits. Conflicts
    /// leave the revert in progress.
    /// </summary>
    public Task<OperationOutcome> RevertAsync(GitCommit commit, CancellationToken ct = default) =>
        PickAsync([.. NoEditor, "revert", "--no-edit", .. MainlineArgs(commit), commit.Id], RepositoryOperation.Reverting, ct);

    /// <summary>A merge commit has several parents: "-m 1" says to compare with the first (the branch merged into).</summary>
    private static string[] MainlineArgs(GitCommit commit) => commit.IsMerge ? ["-m", "1"] : [];

    private async Task<OperationOutcome> PickAsync(IReadOnlyList<string> args, RepositoryOperation operation, CancellationToken ct)
    {
        var outcome = await StartOperationAsync(args, ct);
        if (outcome != OperationOutcome.Conflicts || await GetOperationAsync(ct) != operation)
            return outcome;

        // Stopped without a conflict and with nothing staged: the changes are already on this
        // branch ("the previous cherry-pick is now empty"). Nothing to commit, so step over it.
        var status = await GetStatusAsync(ct);
        if (status.Changes.Any(c => c.IsConflicted || c.IsStaged))
            return outcome;
        var skip = operation == RepositoryOperation.CherryPicking ? "cherry-pick" : "revert";
        await Runner.RunAsync(RepositoryPath, [skip, "--skip"], ct: ct);
        return OperationOutcome.UpToDate;
    }

    // ---- Blame -----------------------------------------------------------------------------------

    /// <summary>
    /// Who last changed each line of <paramref name="path"/> (<c>blame --porcelain</c>): as the
    /// file is on disk now when <paramref name="commitId"/> is null (lines changed since the last
    /// commit show as uncommitted), else as it was in that commit.
    /// </summary>
    public async Task<IReadOnlyList<BlameLine>> GetBlameAsync(string path, string? commitId = null, CancellationToken ct = default)
    {
        List<string> args = ["blame", "--porcelain"];
        if (commitId is not null)
            args.Add(commitId);
        args.AddRange(["--", path]);
        var result = await Runner.RunAsync(RepositoryPath, args, new GitRunOptions { ReadOnly = true }, ct);
        return BlameParser.Parse(result.StandardOutput);
    }

    // ---- Submodules ------------------------------------------------------------------------------

    /// <summary>The submodules and where each stands; empty (without running git) when there's no .gitmodules.</summary>
    public async Task<IReadOnlyList<GitSubmodule>> GetSubmodulesAsync(CancellationToken ct = default)
    {
        if (!File.Exists(Path.Combine(RepositoryPath, ".gitmodules")))
            return [];
        var result = await Runner.RunAsync(RepositoryPath, ["submodule", "status"], new GitRunOptions { ReadOnly = true }, ct);
        return SubmoduleParser.Parse(result.StandardOutput);
    }

    /// <summary>
    /// Clones missing submodules and checks every submodule out at the commit this repository
    /// records (<c>submodule update --init --recursive</c>). Network: progress + cancellation.
    /// A submodule's own uncommitted changes make git stop rather than overwrite them.
    /// </summary>
    public Task UpdateSubmodulesAsync(IProgress<GitProgress>? progress = null, CancellationToken ct = default) =>
        RunRemoteAsync(["submodule", "update", "--init", "--recursive", "--progress"], progress, ct);

    /// <summary>Full folder of a submodule (to open it as its own repository tab).</summary>
    public string SubmoduleFolder(GitSubmodule submodule) =>
        Path.GetFullPath(Path.Combine(RepositoryPath, submodule.Path.Replace('/', Path.DirectorySeparatorChar)));
}
