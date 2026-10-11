using DevHub.Git.Core;
using DevHub.Integration;
using DevHub.Services;
using DevHub.ViewModels.Dialogs;
using DevHub.ViewModels.Documents;

namespace DevHub.ViewModels.Git;

/// <summary>
/// Git → Clone Repository…: the dialog, then the clone in the status bar (progress, Cancel),
/// then the new repository opens as a tab. With the Projects plugin on, the clone goes into a
/// project folder by default and Projects rescans when it landed in one (<see cref="IProjectFolders"/>).
/// One clone at a time.
/// </summary>
/// <param name="projectFolders">Offered by the Projects plugin (empty when it's off).</param>
/// <param name="host">The hosting service (GitHub) for "your repositories"; null when no hosting plugin is on (clone by URL only).</param>
public sealed class CloneOperations(IDialogService dialogs, IStatusService status,
    IDocumentService documents, IEnumerable<IProjectFolders> projectFolders, GitRunner? runner = null,
    IRemoteHost? host = null)
{
    private bool _busy;

    public async Task CloneAsync()
    {
        if (runner is null)
        {
            status.Show("Git is not installed, so DevHub can't clone.");
            return;
        }
        if (_busy)
        {
            status.Show("A clone is already running.");
            return;
        }

        var projects = projectFolders.FirstOrDefault();
        var scanFolders = projects?.Folders ?? [];
        var folder = scanFolders.FirstOrDefault(Directory.Exists)
                     ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dialog = new CloneDialogViewModel(host, dialogs, folder);
        if (!await dialogs.ShowDialogAsync(dialog))
            return;

        var target = dialog.TargetPath;
        var name = Path.GetFileName(target);
        _busy = true;
        using var cts = new CancellationTokenSource();
        var task = status.StartTask($"Cloning {name}…", cts.Cancel);
        try
        {
            await GitCloner.CloneAsync(runner, dialog.CloneUrl, target, new StatusProgress(task), cts.Token);
        }
        catch (OperationCanceledException)
        {
            status.Show($"Clone of {name} cancelled; nothing was left behind.");
            return;
        }
        catch (RemoteOperationException ex)
        {
            status.Show($"Clone of {name}: {ex.Error.Title}.");
            await dialogs.ShowDialogAsync(new ConfirmDialogViewModel(ex.Error.Title, ex.Error.Explanation, "OK",
                ex.GitOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(l => ProgressParser.Parse(l) is null).ToList(),
                destructive: false, showCancel: false));
            return;
        }
        catch (Exception ex)
        {
            status.Show($"Clone of {name} failed: {ex.Message}");
            return;
        }
        finally
        {
            task.Dispose();
            _busy = false;
        }

        documents.OpenById(RepositoryDocumentFactory.IdFor(target));
        var inScanFolder = scanFolders.Any(f => IsInside(target, f));
        if (inScanFolder)
            await projects!.RescanAsync();
        status.Show(projects is null || inScanFolder
            ? $"Cloned {name} into {target}."
            : $"Cloned {name} into {target}. (Not in a Projects folder: add {Path.GetDirectoryName(target)} there to list it.)");
    }

    private static bool IsInside(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
