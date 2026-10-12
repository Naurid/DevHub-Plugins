using DevHub.Git.Core;
using DevHub.Services;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Git;

/// <summary>
/// What you can do with branches, in one place, so the Branches / Remotes sections, the
/// title-bar popup and the Git actions all behave the same: checkout (with Smart Checkout
/// when local changes are in the way), new, rename, delete (asking first). Every operation
/// reports in the status bar and refreshes the repository.
/// </summary>
public sealed class BranchOperations(GitRepositoryClient? client, IDialogService dialogs, IStatusService status,
    Func<Task> refresh)
{
    /// <summary>The branches as last loaded (for "already exists" checks).</summary>
    public IReadOnlyList<GitBranch> Branches { get; set; } = [];

    public bool IsAvailable => client is not null;

    public async Task CheckoutAsync(GitBranch branch)
    {
        if (client is null || branch.IsCurrent)
            return;

        await RunAsync($"Checkout {branch.ShortName}", async () =>
        {
            if (await client.CheckoutAsync(branch) == CheckoutResult.Done)
            {
                status.Show($"Checked out {LocalName(branch)}.");
                return;
            }

            // JetBrains' Smart Checkout: nothing is lost either way, so it isn't "destructive".
            var smart = new ConfirmDialogViewModel("Local Changes in the Way",
                $"Your local changes would be overwritten by checking out {branch.ShortName}. Stash them, " +
                $"check out {branch.ShortName}, and put the changes back on top (Smart Checkout)?",
                "Smart Checkout", destructive: false);
            if (!await dialogs.ShowDialogAsync(smart))
            {
                status.Show("Checkout cancelled; nothing changed.");
                return;
            }

            status.Show(await client.SmartCheckoutAsync(branch) == SmartCheckoutResult.Done
                ? $"Checked out {LocalName(branch)}; your local changes came along."
                : $"Checked out {LocalName(branch)}, but your changes conflicted when re-applied. " +
                  "The conflicts are marked in the files, and the changes are also kept as a stash.");
        });
    }

    /// <summary>New branch from <paramref name="startPoint"/> (null = the current commit), asking for the name.</summary>
    public Task NewBranchAsync(GitBranch? startPoint = null) =>
        NewBranchAtAsync(startPoint?.ShortName, startPoint?.ShortName, startPoint is { IsRemote: true } ? startPoint.Name : "");

    /// <summary>New branch at <paramref name="from"/> (a branch, tag or commit id; null = the current commit), asking for the name.</summary>
    /// <param name="label">How the dialog title names the start point ("v1.0", "1a2b3c4d").</param>
    public async Task NewBranchAtAsync(string? from, string? label, string initialName = "")
    {
        if (client is null)
            return;

        var dialog = new BranchNameDialogViewModel(client, Branches,
            title: label is null ? "New Branch" : $"New Branch from {label}",
            initialName: initialName,
            showCheckout: true);
        if (!await dialogs.ShowDialogAsync(dialog))
            return;

        await RunAsync("Create branch", async () =>
        {
            await client.CreateBranchAsync(dialog.Name, from, dialog.Checkout);
            status.Show(dialog.Checkout ? $"Created and checked out {dialog.Name}." : $"Created {dialog.Name}.");
        });
    }

    public async Task RenameAsync(GitBranch branch)
    {
        if (client is null || branch.IsRemote)
            return;

        var dialog = new BranchNameDialogViewModel(client, Branches.Where(b => b.Ref != branch.Ref).ToList(),
            title: $"Rename {branch.Name}", initialName: branch.Name, showCheckout: false, okText: "Rename");
        if (!await dialogs.ShowDialogAsync(dialog) || dialog.Name == branch.Name)
            return;

        await RunAsync("Rename branch", async () =>
        {
            await client.RenameBranchAsync(branch.Name, dialog.Name);
            status.Show($"Renamed {branch.Name} to {dialog.Name}.");
        });
    }

    /// <summary>Deletes a local branch after asking; unmerged commits get a second, explicit warning.</summary>
    public async Task DeleteAsync(GitBranch branch)
    {
        if (client is null || branch.IsRemote || branch.IsCurrent)
            return;

        var confirm = new ConfirmDialogViewModel("Delete Branch",
            $"Delete the branch {branch.Name}? Its commits stay wherever they're also on another branch.", "Delete");
        if (!await dialogs.ShowDialogAsync(confirm))
            return;

        await RunAsync("Delete branch", async () =>
        {
            if (await client.DeleteBranchAsync(branch.Name))
            {
                status.Show($"Deleted {branch.Name}.");
                return;
            }

            // The Git rules: say exactly what is lost. git's "not fully merged" means not merged
            // into the current branch (or the branch's upstream); the commits may still be on some
            // other branch, so don't claim more than that.
            var current = Branches.FirstOrDefault(b => b.IsCurrent)?.Name ?? "the current commit";
            var force = new ConfirmDialogViewModel("Branch Not Merged",
                $"{branch.Name} has commits that aren't in {current} (last: \"{branch.Subject}\"). " +
                "If no other branch has them either, deleting it loses them; git can then only find them " +
                "through the reflog, for a few weeks.",
                "Delete Anyway");
            if (!await dialogs.ShowDialogAsync(force))
            {
                status.Show($"Kept {branch.Name}.");
                return;
            }
            await client.DeleteBranchAsync(branch.Name, force: true);
            status.Show($"Deleted {branch.Name} (with its unmerged commits).");
        });
    }

    /// <summary>Called when a merge / rebase stops on conflicts (the tab then shows Local Changes).</summary>
    public Action? ShowConflicts { get; set; }

    /// <summary>JetBrains' "Merge into Current": brings <paramref name="branch"/>'s commits into the current branch.</summary>
    public async Task MergeIntoCurrentAsync(GitBranch branch)
    {
        if (client is null || branch.IsCurrent)
            return;
        var current = Branches.FirstOrDefault(b => b.IsCurrent)?.Name ?? "the current commit";

        var confirm = new ConfirmDialogViewModel("Merge",
            $"Merge {branch.ShortName} into {current}? If both changed the same lines, the merge stops so you can " +
            "resolve the conflicts (or abort, which puts everything back).", "Merge", destructive: false);
        if (!await dialogs.ShowDialogAsync(confirm))
            return;

        await RunAsync("Merge", async () => Report(await client.MergeAsync(branch.ShortName),
            done: $"Merged {branch.ShortName} into {current}.",
            upToDate: $"{current} already contains everything in {branch.ShortName}.",
            conflicts: $"The merge of {branch.ShortName} stopped on conflicts. Resolve them in Changes, then Continue."));
    }

    /// <summary>
    /// JetBrains' "Rebase Current onto Selected": replays the current branch's own commits on top
    /// of <paramref name="branch"/>. Rewrites those commits, so the dialog says so.
    /// </summary>
    public async Task RebaseCurrentOntoAsync(GitBranch branch)
    {
        if (client is null || branch.IsCurrent)
            return;
        var current = Branches.FirstOrDefault(b => b.IsCurrent)?.Name ?? "the current commit";

        var confirm = new ConfirmDialogViewModel("Rebase",
            $"Rebase {current} onto {branch.ShortName}? Your commits on {current} are replayed on top of it as new " +
            "commits. If they were already pushed, pushing again needs Force Push, which affects anyone who pulled them.",
            "Rebase", destructive: false);
        if (!await dialogs.ShowDialogAsync(confirm))
            return;

        await RunAsync("Rebase", async () => Report(await client.RebaseAsync(branch.ShortName),
            done: $"Rebased {current} onto {branch.ShortName}.",
            upToDate: $"{current} is already on top of {branch.ShortName}.",
            conflicts: "The rebase stopped on conflicts. Resolve them in Changes, then Continue (or Skip that commit)."));
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
            ShowConflicts?.Invoke();
    }

    private async Task RunAsync(string what, Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            status.Show($"{what} failed: {(ex is GitException git && git.StandardError.Trim() is { Length: > 0 } e ? FirstLine(e) : ex.Message)}");
        }
        await refresh();
    }

    // "error: …\nhint: …": the first line says it; hints are in the Git Console.
    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    private static string LocalName(GitBranch branch) => branch.Name;
}
