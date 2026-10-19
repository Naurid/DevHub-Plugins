using CommunityToolkit.Mvvm.Input;
using DevHub.Ci.Core;
using DevHub.Sdk.Core.Localization;
using DevHub.Services;
using DevHub.Services.Ci;
using DevHub.ViewModels.Ci;

namespace DevHub.Actions;

/// <summary>CI's actions (Find Action, the CI menu): refresh the runs of the repository in front, re-run its branch's failed jobs.</summary>
public sealed class CiActionProvider(CiService service, CiToolWindowViewModel window, IStatusService status) : IActionProvider
{
    public IEnumerable<AppAction> GetActions()
    {
        yield return new AppAction("Ci.Refresh", L.T("Refresh CI Runs"), new RelayCommand(Refresh))
        {
            Category = "CI",
            IconKind = Controls.IconKind.Refresh,
            Menu = "CI",
            MenuOrder = 0,
            Description = L.T("Ask GitHub for the runs of the repository in front now."),
        };
        yield return new AppAction("Ci.RerunFailed", L.T("Re-run Failed Jobs…"), new AsyncRelayCommand(RerunFailedAsync))
        {
            Category = "CI",
            IconKind = Controls.IconKind.Run,
            Menu = "CI",
            MenuOrder = 1,
            Description = L.T("Run the failed jobs of the current branch's latest failed run again."),
        };
    }

    private void Refresh()
    {
        if (service.Repository(service.ActiveFolder) is { } repo)
        {
            service.PollSoon(repo);
            status.Show(L.T($"Asking GitHub for the runs of {repo.Slug}…"));
        }
        else
            status.Show(L.T("Open a repository tab of a GitHub repository first."));
    }

    private async Task RerunFailedAsync()
    {
        var folder = service.ActiveFolder;
        var branch = folder is null ? null : GitHubRemote.CurrentBranch(folder);
        var failed = service.Repository(folder) is { } repo && branch is not null
            ? CiService.LatestOf(repo.Runs, branch).FirstOrDefault(r => r.State is RunState.Failed or RunState.Cancelled)
            : null;
        if (failed is null)
        {
            status.Show(L.T("No failed run on the current branch."));
            return;
        }
        await window.RerunFailedAsync(failed);
    }
}
