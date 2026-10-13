using DevHub.Board.Core;
using DevHub.Board.Core.Storage;
using DevHub.ViewModels.Dialogs;
using DevHub.ViewModels.Documents;

namespace DevHub.Services.Board;

/// <summary>Opening boards: New Board, Open Board (a folder), a recent board, the board of the project in front.</summary>
/// <remarks>
/// The document service is looked up when first needed, not injected: the shell builds it from the documents, and
/// the Boards tool window (which uses this) is built early; taking it in the constructor hangs startup
/// (docs/UI_GUIDE.md §8, "no window ever appears").
/// </remarks>
public sealed class BoardOpener(BoardService boards, IDialogService dialogs, IServiceProvider services, IStatusService status)
{
    private IDocumentService Documents => (IDocumentService)services.GetService(typeof(IDocumentService))!;

    /// <summary>Asks for a new board and opens its tab.</summary>
    public async Task NewBoardAsync(BoardLocation? suggested = null)
    {
        var dialog = new NewBoardDialogViewModel(boards, suggested, dialogs);
        if (!await dialogs.ShowDialogAsync(dialog) || dialog.Created is not { } created)
            return;
        var location = created.Location;
        // The tab acquires the board itself; the dialog's hold on it ends here.
        Documents.OpenById(BoardDocumentFactory.IdFor(location));
        boards.Release(created);
    }

    /// <summary>Asks for a folder and opens the board in it (any store that finds one there).</summary>
    public async Task OpenBoardAsync()
    {
        var folder = await dialogs.PickFolderAsync("Folder with a board (or a repository with a devhub/board branch)");
        if (folder is null)
            return;
        if (await FindAsync(folder) is { } location)
        {
            Documents.OpenById(BoardDocumentFactory.IdFor(location));
            return;
        }
        var create = await dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Open Board",
            $"There's no board in {folder}. Create one there?", "Create Board…", [folder], destructive: false));
        if (create)
            await NewBoardAsync(new BoardLocation(FolderBoardStoreProvider.KindId, folder));
    }

    /// <summary>Opens a board from the recent list (and drops it from the list when it's gone).</summary>
    public void OpenRecent(RecentBoard recent)
    {
        if (boards.Provider(recent.StoreKind) is null)
        {
            status.Show($"{recent.Name} needs a plugin that's switched off.");
            return;
        }
        if (recent.StoreKind == FolderBoardStoreProvider.KindId && !FolderBoardStore.HasBoard(recent.Location))
        {
            status.Show($"{recent.Name} isn't in {recent.Location} any more; removed from the list.");
            boards.Forget(recent);
            return;
        }
        Documents.OpenById(BoardDocumentFactory.IdFor(new BoardLocation(recent.StoreKind, recent.Location)));
    }

    /// <summary>
    /// The board of the project / repository in front (its tab's folder): opens it, or offers to create one there
    /// (on its devhub/board branch when Board.Git is on, else in <c>.devhub/board</c>).
    /// </summary>
    public async Task BoardForProjectAsync()
    {
        var folder = Documents.Active?.ContextPath;
        if (folder is null || !Directory.Exists(folder))
        {
            status.Show("Open a project or repository tab first (its folder is where the board goes).");
            return;
        }
        if (Documents.Active is BoardDocumentViewModel)
            return;
        if (await FindAsync(folder) is { } existing)
        {
            Documents.OpenById(BoardDocumentFactory.IdFor(existing));
            return;
        }
        var inFolder = Path.Combine(folder, ".devhub", "board");
        if (FolderBoardStore.HasBoard(inFolder))
        {
            Documents.OpenById(BoardDocumentFactory.IdFor(new BoardLocation(FolderBoardStoreProvider.KindId, inFolder)));
            return;
        }
        // Prefer a store another plugin offers for this folder (the repository branch), else a folder inside it.
        foreach (var provider in boards.Providers.Where(p => p.Kind != FolderBoardStoreProvider.KindId))
        {
            if (await provider.CanUseAsync(folder))
            {
                await NewBoardAsync(new BoardLocation(provider.Kind, folder));
                return;
            }
        }
        await NewBoardAsync(new BoardLocation(FolderBoardStoreProvider.KindId, inFolder));
    }


    /// <summary>
    /// Copies a board to another store (a folder → the repository's devhub/board branch, or back) and opens the copy.
    /// The original stays where it is, so nothing is lost; the message says where.
    /// </summary>
    public async Task CopyBoardAsync(BoardContext context)
    {
        var kinds = boards.Providers.ToList();
        var dialog = new TextInputDialogViewModel("Copy Board To…", "", "", "Choose Folder…")
        {
            ShowText = false,
            Explanation = $"Copies {context.Project.Key} with everything in it (issues, history, attachments) to another place and opens the copy. "
                          + $"The board stays in {context.Session.Store.DisplayLocation} too.",
            Choice = new ChoiceInput("Keep the copy", kinds.Select(k => k.DisplayName).ToList(), kinds.FirstOrDefault(k => k.Kind != context.Location.StoreKind)?.DisplayName ?? kinds[0].DisplayName),
        };
        if (!await dialogs.ShowDialogAsync(dialog))
            return;
        var provider = kinds.First(k => k.DisplayName == dialog.Choice!.Selected);
        var folder = await dialogs.PickFolderAsync(provider.Kind == FolderBoardStoreProvider.KindId ? "Folder for the copy" : "Repository folder");
        if (folder is null)
            return;
        if (!await provider.CanUseAsync(folder))
        {
            status.Show(provider.Kind == FolderBoardStoreProvider.KindId ? "That folder can't hold a board." : "That folder isn't a git repository.");
            return;
        }
        var target = new BoardLocation(provider.Kind, Path.GetFullPath(folder));
        if (target.Key.Equals(context.Location.Key, StringComparison.OrdinalIgnoreCase))
        {
            status.Show("That's where the board already is.");
            return;
        }
        try
        {
            using var store = provider.Open(target.Location);
            if (await store.LoadAsync() is not null)
            {
                status.Show("There's already a board there.");
                return;
            }
            var files = context.Snapshot.Files.ToDictionary(f => f.Key, f => (byte[]?)f.Value, StringComparer.Ordinal);
            await store.WriteAsync(new BoardChange($"Board {context.Project.Key} copied from {context.Session.Store.DisplayLocation}", files), BoardFiles.NoBoard);
        }
        catch (Exception e) when (e is BoardStoreException or BoardConflictException or IOException or UnauthorizedAccessException)
        {
            await dialogs.ShowDialogAsync(new ConfirmDialogViewModel("Copy Board", "The board couldn't be copied.", "OK", [e.Message], destructive: false, showCancel: false));
            return;
        }
        Documents.OpenById(BoardDocumentFactory.IdFor(target));
        status.Show($"Board copied. The original is still in {context.Session.Store.DisplayLocation}: delete it there when you no longer need it.");
    }
    /// <summary>The board in a folder: a folder store's board.json, or what another store finds there.</summary>
    public async Task<BoardLocation?> FindAsync(string folder)
    {
        if (FolderBoardStore.HasBoard(folder))
            return new BoardLocation(FolderBoardStoreProvider.KindId, Path.GetFullPath(folder));
        foreach (var provider in boards.Providers.Where(p => p.Kind != FolderBoardStoreProvider.KindId))
        {
            try
            {
                if (!await provider.CanUseAsync(folder))
                    continue;
                using var store = provider.Open(folder);
                if (await store.LoadAsync() is not null)
                    return new BoardLocation(provider.Kind, Path.GetFullPath(folder));
            }
            catch (Exception e) when (e is BoardStoreException or IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }
}
