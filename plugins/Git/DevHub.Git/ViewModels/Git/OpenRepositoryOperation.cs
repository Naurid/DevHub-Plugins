using DevHub.Git.Core;
using DevHub.Services;
using DevHub.ViewModels.Documents;

namespace DevHub.ViewModels.Git;

/// <summary>
/// Git → Open Repository…: pick a folder and open its repository as a tab (a subfolder opens
/// the repository it's in). Git's own way in, so it works without the Projects plugin.
/// </summary>
public sealed class OpenRepositoryOperation(IDialogService dialogs, IStatusService status, IDocumentService documents,
    GitRunner? runner = null)
{
    public async Task OpenAsync()
    {
        if (runner is null)
        {
            status.Show("Git is not installed, so DevHub can't open repositories.");
            return;
        }
        if (await dialogs.PickFolderAsync("Open which repository?") is not { } folder)
            return;

        var client = new GitRepositoryClient(runner, folder);
        if (!await client.IsRepositoryAsync())
        {
            status.Show($"{folder} isn't in a git repository. (Clone one, or run git init there.)");
            return;
        }
        documents.OpenById(RepositoryDocumentFactory.IdFor(await client.GetTopLevelAsync()));
    }
}
