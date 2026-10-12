using CommunityToolkit.Mvvm.ComponentModel;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Git;

/// <summary>
/// Stash Changes (JetBrains' dialog): an optional message, whether new (untracked) files go
/// along, and whether staged changes also stay in place.
/// </summary>
public sealed partial class StashDialogViewModel : DialogViewModelBase
{
    public override string Title => "Stash Changes";
    public override string OkText => "Stash";
    public override bool FitsContent => true;
    public override double Width => 440;
    public override double MinWidth => 360;
    public override double MinHeight => 0;

    /// <summary>Optional; git names an unnamed stash after the commit it was made on.</summary>
    [ObservableProperty] private string _message = "";

    /// <summary>Take new, not-yet-tracked files too (on by default: otherwise they stay behind, which surprises people).</summary>
    [ObservableProperty] private bool _includeUntracked = true;

    /// <summary>Stash everything but leave the staged changes in the working tree as well.</summary>
    [ObservableProperty] private bool _keepIndex;
}
