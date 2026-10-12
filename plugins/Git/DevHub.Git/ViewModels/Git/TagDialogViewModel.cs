using CommunityToolkit.Mvvm.ComponentModel;
using DevHub.Git.Core;
using DevHub.ViewModels.Dialogs;

namespace DevHub.ViewModels.Git;

/// <summary>
/// New Tag: a name (checked while typing: empty, spaces, already exists; OK then runs git's own
/// rules) and an optional message. With a message the tag is annotated (it records who tagged
/// and when; what releases usually use), without one it's a lightweight tag (just a name).
/// </summary>
public sealed partial class TagDialogViewModel : DialogViewModelBase
{
    private readonly GitRepositoryClient _client;
    private readonly IReadOnlyList<GitTag> _existing;

    /// <param name="target">What is tagged, for the dialog ("1a2b3c4d Fix login", "the current commit").</param>
    public TagDialogViewModel(GitRepositoryClient client, IReadOnlyList<GitTag> existing, string target)
    {
        _client = client;
        _existing = existing;
        Target = target;
    }

    public override string Title => "New Tag";
    public override string OkText => "Create Tag";
    public override bool FitsContent => true;
    public override double Width => 460;
    public override double MinWidth => 360;
    public override double MinHeight => 0;

    /// <summary>"Tag 1a2b3c4d Fix login".</summary>
    public string Target { get; }

    [ObservableProperty] private string _name = "";

    /// <summary>Optional: makes it an annotated tag.</summary>
    [ObservableProperty] private string _message = "";

    [ObservableProperty] private string? _error;

    partial void OnNameChanged(string value) => Validate();

    private void Validate()
    {
        var name = Name.Trim();
        Error = name.Length == 0 ? null
            : name.Any(char.IsWhiteSpace) ? "Tag names can't contain spaces."
            : _existing.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                ? $"A tag named {name} already exists."
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
        if (!await _client.IsValidTagNameAsync(Name))
        {
            Error = "git doesn't allow that name (no \"..\", \"~\", \"^\", \":\", \"?\", \"*\", \"[\", \"\\\", a leading \"-\" or a trailing \".lock\").";
            return false;
        }
        return true;
    }
}
