using CommunityToolkit.Mvvm.ComponentModel;
using DevHub.Git.Core;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Git;

/// <summary>
/// Asks for a branch name (New Branch, Rename). Common mistakes are flagged while typing
/// (empty, spaces, already exists); OK then runs git's own rules (<c>check-ref-format</c>)
/// and stays open with the reason if the name is refused.
/// </summary>
public sealed partial class BranchNameDialogViewModel : DialogViewModelBase
{
    private readonly GitRepositoryClient _client;
    private readonly IReadOnlyList<GitBranch> _existing;

    /// <param name="existing">Branches the name must not clash with.</param>
    /// <param name="showCheckout">Show "Check out the new branch" (New Branch, not Rename).</param>
    public BranchNameDialogViewModel(GitRepositoryClient client, IReadOnlyList<GitBranch> existing,
        string title, string initialName, bool showCheckout, string okText = "Create")
    {
        _client = client;
        _existing = existing;
        Title = title;
        OkText = okText;
        ShowCheckout = showCheckout;
        _name = initialName;
        Validate();
    }

    public override string Title { get; }
    public override string OkText { get; }
    public override bool FitsContent => true;
    public override double Width => 420;
    public override double MinWidth => 340;
    public override double MinHeight => 0;

    [ObservableProperty] private string _name;

    /// <summary>Switch to the new branch after creating it (like JetBrains' default).</summary>
    [ObservableProperty] private bool _checkout = true;

    public bool ShowCheckout { get; }

    /// <summary>Why the name can't be used, or null.</summary>
    [ObservableProperty] private string? _error;

    partial void OnNameChanged(string value) => Validate();

    private void Validate()
    {
        var name = Name.Trim();
        Error = name.Length == 0 ? null // nothing typed yet: no red text, but OK won't accept it
            : name.Any(char.IsWhiteSpace) ? "Branch names can't contain spaces."
            : _existing.Any(b => !b.IsRemote && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
                ? $"A branch named {name} already exists."
            : null;
    }

    protected override async Task<bool> ApplyChangesAsync()
    {
        Name = Name.Trim();
        Validate();
        if (Name.Length == 0)
        {
            Error = "Enter a name.";
            return false;
        }
        if (Error is not null)
            return false;
        if (!await _client.IsValidBranchNameAsync(Name))
        {
            Error = "git doesn't allow that name (no \"..\", \"~\", \"^\", \":\", \"?\", \"*\", \"[\", \"\\\", a leading \"-\" or a trailing \".lock\").";
            return false;
        }
        return true;
    }
}
