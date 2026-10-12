using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Sdk.Core.Formatting;
using DevHub.Git.Core;
using DevHub.Sdk.Core.Search;

namespace DevHub.ViewModels.Git;

/// <summary>
/// The Tags section of a repository tab (G8): every tag, newest first, with the commit it marks
/// and its message. New Tag (on the current commit; on another commit from All Commits), Push
/// Tag, Delete (asks), Delete from Remote (asks), New Branch from a tag.
/// </summary>
public sealed partial class TagListViewModel : ViewModelBase
{
    private readonly CommitOperations _operations;
    private readonly BranchOperations _branches;
    private IReadOnlyList<GitTag> _tags = [];

    public TagListViewModel(CommitOperations operations, BranchOperations branches)
    {
        _operations = operations;
        _branches = branches;
    }

    public ObservableCollection<TagItemViewModel> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    public int Count => _tags.Count;

    public string EmptyText => !string.IsNullOrWhiteSpace(Filter) ? "No tag matches."
        : "No tags yet. Tag a commit (usually a release) with New Tag, or right-click a commit in All Commits.";

    [ObservableProperty] private string _filter = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PushCommand), nameof(DeleteCommand), nameof(DeleteFromRemoteCommand), nameof(NewBranchFromCommand))]
    private TagItemViewModel? _selected;

    partial void OnFilterChanged(string value) => Rebuild();

    /// <summary>Takes the freshly loaded tags, keeping the selection.</summary>
    public void Update(IReadOnlyList<GitTag> tags)
    {
        if (tags.SequenceEqual(_tags))
            return; // nothing changed (most refreshes are file edits)
        _tags = tags;
        Rebuild();
        OnPropertyChanged(nameof(Count));
    }

    private void Rebuild()
    {
        var keep = Selected?.Tag.Name;
        var now = DateTimeOffset.Now;
        var filtering = !string.IsNullOrWhiteSpace(Filter);

        var rows = _tags
            .Select(t => (Tag: t, Match: filtering ? FuzzyMatcher.Match(Filter, t.Name) : null))
            .Where(x => !filtering || x.Match is not null)
            // Filtering: best match first; otherwise newest first (git's order).
            .OrderByDescending(x => x.Match?.Score ?? 0)
            .Select(x => new TagItemViewModel(x.Tag, now));

        Items.Clear();
        foreach (var row in rows)
            Items.Add(row);

        Selected = Items.FirstOrDefault(i => i.Tag.Name == keep);
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(EmptyText));
    }

    private bool HasSelection() => Selected is not null && _operations.IsAvailable;

    /// <summary>Tag the current commit (other commits: right-click them in All Commits).</summary>
    [RelayCommand]
    private Task NewTagAsync() => _operations.NewTagAsync(null);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task PushAsync() => _operations.PushTagAsync(Selected!.Tag);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteAsync() => _operations.DeleteTagAsync(Selected!.Tag);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteFromRemoteAsync() => _operations.DeleteRemoteTagAsync(Selected!.Tag);

    /// <summary>A branch starting at the tagged commit (e.g. a fix for a release).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task NewBranchFromAsync() => _branches.NewBranchAtAsync(Selected!.Tag.Name, Selected.Tag.Name);
}

/// <summary>A tag row: name, the commit's short id, message / subject, and when.</summary>
public sealed class TagItemViewModel(GitTag tag, DateTimeOffset now)
{
    public GitTag Tag { get; } = tag;
    public string Name => Tag.Name;
    public string ShortId => Tag.ShortId;
    public string Subject => Tag.Subject;
    public string When { get; } = tag.Date == default ? "" : RelativeTime.Format(tag.Date, now);

    /// <summary>"annotated" badge: the tag has its own message (else it shows the commit's subject).</summary>
    public bool IsAnnotated => Tag.IsAnnotated;

    public string ToolTip => (Tag.IsAnnotated ? $"Annotated tag {Tag.Name}" : $"Lightweight tag {Tag.Name}") +
                             $" on {Tag.ShortId}\n{Tag.Subject}";

    public override string ToString() => Tag.IsAnnotated ? $"{Name} (annotated)" : Name;
}
