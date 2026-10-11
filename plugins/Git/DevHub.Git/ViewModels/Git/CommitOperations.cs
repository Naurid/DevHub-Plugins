using DevHub.Git.Core;
using DevHub.Services;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Git;

/// <summary>
/// What you can do with a commit or a tag (G8), in one place, so All Commits' menu and the Tags
/// section behave the same: cherry-pick, revert, new branch / tag at a commit; delete and push
/// tags. Every operation reports in the status bar and refreshes the repository; one that stops
/// on conflicts brings up Local Changes, where the banner offers Continue / Abort.
/// </summary>
public sealed class CommitOperations(GitRepositoryClient? client, IDialogService dialogs, IStatusService status,
    Func<Task> refresh, BranchOperations branches, RemoteOperations remote)
{
    /// <summary>The tags as last loaded (for "already exists" checks).</summary>
    public IReadOnlyList<GitTag> Tags { get; set; } = [];

    public bool IsAvailable => client is not null;

    /// <summary>
    /// JetBrains' Cherry-Pick: copies the commit's changes onto the current branch as a new commit.
    /// Adds a commit and loses nothing, so it doesn't ask (like JetBrains).
    /// </summary>
    public async Task CherryPickAsync(GitCommit commit)
    {
        if (client is null)
            return;
        await RunAsync("Cherry-pick", async () => Report(await client.CherryPickAsync(commit),
            done: $"Cherry-picked {commit.ShortId} \"{commit.Subject}\" onto the current branch.",
            upToDate: $"The current branch already has the changes of {commit.ShortId}; nothing to do.",
            conflicts: $"Cherry-picking {commit.ShortId} stopped on conflicts. Resolve them in Changes, then Continue (or Abort)."));
    }

    /// <summary>
    /// JetBrains' Revert Commit: a new commit that undoes this one. History isn't rewritten (safe
    /// on pushed commits) and the new commit can itself be reverted, so it doesn't ask.
    /// </summary>
    public async Task RevertAsync(GitCommit commit)
    {
        if (client is null)
            return;
        await RunAsync("Revert", async () => Report(await client.RevertAsync(commit),
            done: $"Reverted {commit.ShortId}: a new commit undoes \"{commit.Subject}\".",
            upToDate: $"The changes of {commit.ShortId} are already undone; nothing to do.",
            conflicts: $"Reverting {commit.ShortId} stopped on conflicts. Resolve them in Changes, then Continue (or Abort)."));
    }

    /// <summary>New branch starting at <paramref name="commit"/>.</summary>
    public Task NewBranchAtAsync(GitCommit commit) => branches.NewBranchAtAsync(commit.Id, commit.ShortId);

    /// <summary>New tag on <paramref name="commit"/> (null = the current commit), asking for the name and an optional message.</summary>
    public async Task NewTagAsync(GitCommit? commit)
    {
        if (client is null)
            return;

        var dialog = new TagDialogViewModel(client, Tags,
            commit is null ? "Tag the current commit" : $"Tag {commit.ShortId} {commit.Subject}");
        if (!await dialogs.ShowDialogAsync(dialog))
            return;

        await RunAsync("Create tag", async () =>
        {
            await client.CreateTagAsync(dialog.Name, commit?.Id, dialog.Message);
            status.Show($"Created tag {dialog.Name}{(string.IsNullOrWhiteSpace(dialog.Message) ? "" : " (annotated)")}. " +
                        "Tags aren't sent by Push: right-click it in Tags → Push Tag.");
        });
    }

    /// <summary>Deletes a local tag after asking (a pushed copy stays on the remote).</summary>
    public async Task DeleteTagAsync(GitTag tag)
    {
        if (client is null)
            return;

        var confirm = new ConfirmDialogViewModel("Delete Tag",
            $"Delete the tag {tag.Name}? The commit it marks stays." +
            (tag.IsAnnotated ? " The tag's own message is lost." : "") +
            " If it was pushed, the remote keeps its copy (Delete from Remote removes that).", "Delete");
        if (!await dialogs.ShowDialogAsync(confirm))
            return;

        await RunAsync("Delete tag", async () =>
        {
            await client.DeleteTagAsync(tag.Name);
            status.Show($"Deleted tag {tag.Name}.");
        });
    }

    /// <summary>Sends a tag to the remote ("origin", or the only remote). Pushing a branch doesn't send tags.</summary>
    public async Task PushTagAsync(GitTag tag)
    {
        if (client is null || await PickRemoteAsync() is not { } target)
            return;
        if (await remote.RunNetworkAsync($"Pushing tag {tag.Name} to {target}", (p, ct) => client.PushTagAsync(target, tag.Name, p, ct)))
            status.Show($"Pushed tag {tag.Name} to {target}.");
    }

    /// <summary>Deletes a tag on the remote after asking; the local tag stays.</summary>
    public async Task DeleteRemoteTagAsync(GitTag tag)
    {
        if (client is null || await PickRemoteAsync() is not { } target)
            return;

        var confirm = new ConfirmDialogViewModel("Delete Tag from Remote",
            $"Delete the tag {tag.Name} on {target}? Everyone fetching from {target} stops seeing it (copies they " +
            "already fetched stay with them). Your local tag stays.", "Delete from Remote");
        if (!await dialogs.ShowDialogAsync(confirm))
            return;

        if (await remote.RunNetworkAsync($"Deleting tag {tag.Name} on {target}",
                (p, ct) => client.DeleteRemoteTagAsync(target, tag.Name, p, ct)))
            status.Show($"Deleted tag {tag.Name} on {target}; your local tag stays.");
    }

    /// <summary>"origin" if there is one, else the first remote; null (and says so) without remotes.</summary>
    private async Task<string?> PickRemoteAsync()
    {
        try
        {
            var remotes = await client!.GetRemotesAsync();
            if (remotes.Count == 0)
                status.Show("This repository has no remote to push to.");
            return remotes.Contains("origin") ? "origin" : remotes.FirstOrDefault();
        }
        catch (Exception ex)
        {
            status.Show("Couldn't read the remotes: " + ex.Message);
            return null;
        }
    }

    private void Report(OperationOutcome outcome, string done, string upToDate, string conflicts)
    {
        status.Show(outcome switch
        {
            OperationOutcome.Done => done,
            OperationOutcome.UpToDate => upToDate,
            _ => conflicts,
        });
        if (outcome == OperationOutcome.Conflicts)
            branches.ShowConflicts?.Invoke();
    }

    private async Task RunAsync(string what, Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            // "error: …\nhint: …": the first line says it; the rest is in the Git Console.
            status.Show($"{what} failed: {(ex is GitException git && git.StandardError.Trim() is { Length: > 0 } e ? e.Split('\n')[0].Trim() : ex.Message)}");
        }
        await refresh();
    }
}
