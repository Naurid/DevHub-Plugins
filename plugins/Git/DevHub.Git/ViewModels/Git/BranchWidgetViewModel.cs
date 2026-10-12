using DevHub.ViewModels.Shell;

namespace DevHub.ViewModels.Git;

/// <summary>
/// The title-bar branch widget (JetBrains' VCS widget): the active repository's branch, ahead /
/// behind, and the branch popup on click. Contributed by the Git plugin; hidden when no
/// repository is active.
/// </summary>
public sealed class BranchWidgetViewModel(ActiveRepositoryService active) : TitleBarWidgetViewModelBase
{
    /// <summary>The view binds <c>Active.Repository.…</c>, so it follows tab switches.</summary>
    public ActiveRepositoryService Active { get; } = active;
}
