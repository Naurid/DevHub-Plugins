using CommunityToolkit.Mvvm.Input;
using DevHub.Actions;
using DevHub.Board;
using DevHub.Board.Core.Storage;
using DevHub.Board.Git.Core;
using DevHub.Git.Core;
using DevHub.Integration;
using DevHub.Sdk.Core.Plugins;
using DevHub.Services;
using DevHub.Services.BoardGit;
using DevHub.ViewModels.BoardGit;
using DevHub.ViewModels.Dialogs;
using DevHub.ViewModels.Documents;
using DevHub.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

[assembly: DevHub.Plugins.DevHubPlugin(typeof(DevHub.Plugins.BoardGitPlugin))]

namespace DevHub.Plugins;

/// <summary>
/// Board.Git: boards on a repository's devhub/board branch, and issues linked to branches and commits. Requires Board
/// and Git. Without git installed it registers nothing (Git's tabs explain how to install it).
/// </summary>
public sealed class BoardGitPlugin : IPlugin
{
    public const string Id = "devhub.board.git";

    public PluginInfo Info { get; } = new(Id, "Board Git",
        "Boards and git together: keep a board on the repository's devhub/board branch (the same on every branch), create a branch from an issue, see an issue's branches and commits, issue keys in the commit list, smart commits.",
        [BoardPlugin.Id, GitPlugin.Id]) { Category = "Planning", Order = 520, Presets = ["Planning"] };

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        if (GitPlugin.Executable.Value is null)
            return;
        services.AddSingleton<IBoardStoreProvider>(sp => new BranchBoardStoreProvider(sp.GetRequiredService<GitRunner>()));
        services.AddSingleton(sp => new DevelopmentQueries(sp.GetRequiredService<GitRunner>()));
        services.AddSingleton<BoardGitServices>();
        services.AddSingleton<IIssueSectionProvider, DevelopmentSectionProvider>();
        services.AddSingleton<IIssueActionProvider, CreateBranchActionProvider>();
        services.AddSingleton<ICommitMessageSuggester, BoardCommitSuggester>();
        services.AddSingleton<IGitDecorator>(sp => new BoardGitDecorator(sp.GetRequiredService<BoardGitServices>(), sp));
        services.AddSingleton<StatusBarWidgetViewModelBase>(sp => new BranchIssueWidgetViewModel(sp.GetRequiredService<BoardGitServices>(), sp));
        services.AddSingleton<IActionProvider, BoardGitActionProvider>();
    }
}

/// <summary>Push Board / Pull Board for a board on the devhub/board branch (the board in front).</summary>
public sealed class BoardGitActionProvider(IServiceProvider services, IStatusService status) : IActionProvider
{
    public IEnumerable<AppAction> GetActions()
    {
        yield return new AppAction("Board.PushBoard", "Push Board", new AsyncRelayCommand(() => OnBranchBoard(PushAsync)))
        {
            Category = "Board",
            IconKind = "push",
            Menu = "Board",
            MenuOrder = 300,
            Description = "Send the devhub/board branch to the remote (backup, or to another machine).",
        };
        yield return new AppAction("Board.PullBoard", "Pull Board", new AsyncRelayCommand(() => OnBranchBoard(PullAsync)))
        {
            Category = "Board",
            IconKind = "update",
            Menu = "Board",
            MenuOrder = 301,
            Description = "Bring the remote's devhub/board branch here (when it's only ahead: nothing here is lost).",
        };
    }

    private async Task OnBranchBoard(Func<BoardDocumentViewModel, string, GitRunner, string, Task> action)
    {
        var documents = (IDocumentService)services.GetService(typeof(IDocumentService))!;
        if (documents.Active is not BoardDocumentViewModel { Context: not null } board || board.Location.StoreKind != BranchBoardStoreProvider.KindId)
        {
            status.Show("Push and Pull Board are for a board kept on a repository's devhub/board branch (the board tab in front).");
            return;
        }
        var runner = services.GetRequiredService<GitRunner>();
        var remotes = (await runner.RunAsync(board.Location.Location, ["remote"], new GitRunOptions { ReadOnly = true })).StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var remote = remotes.Contains("origin") ? "origin" : remotes.FirstOrDefault();
        if (remote is null)
        {
            status.Show("The repository has no remote to push to or pull from.");
            return;
        }
        await action(board, board.Location.Location, runner, remote);
    }

    private async Task PushAsync(BoardDocumentViewModel board, string repository, GitRunner runner, string remote)
    {
        using var task = status.StartTask($"Pushing the board to {remote}…");
        var result = await runner.RunAsync(repository, ["push", remote, BranchBoardStore.BranchName + ":" + BranchBoardStore.BranchName],
            new GitRunOptions { AllowFailure = true });
        status.Show(result.Succeeded ? $"Board pushed to {remote}." : "Push failed: " + FirstLine(result.StandardError));
    }

    private async Task PullAsync(BoardDocumentViewModel board, string repository, GitRunner runner, string remote)
    {
        using (status.StartTask($"Pulling the board from {remote}…"))
        {
            // Fetching into the local branch moves it only when that's a fast-forward: diverged boards stay as they are.
            var result = await runner.RunAsync(repository, ["fetch", remote, BranchBoardStore.BranchName + ":" + BranchBoardStore.BranchName],
                new GitRunOptions { AllowFailure = true });
            if (!result.Succeeded)
            {
                var diverged = result.StandardError.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
                               || result.StandardError.Contains("rejected", StringComparison.OrdinalIgnoreCase);
                await services.GetRequiredService<IDialogService>().ShowDialogAsync(new ConfirmDialogViewModel("Pull Board",
                    diverged
                        ? "The board was changed both here and on the remote. Merging two versions of a board comes later (multi-user); nothing was changed here."
                        : "The board couldn't be pulled.", "OK", [FirstLine(result.StandardError)], destructive: false, showCancel: false));
                return;
            }
        }
        if (board.Context is not null)
            await board.Context.ReloadAsync();
        status.Show($"Board pulled from {remote}.");
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
}
