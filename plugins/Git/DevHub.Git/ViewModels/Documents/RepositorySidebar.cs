using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Controls;
using DevHub.Sdk.Core.Formatting;
using DevHub.Git.Core;
using DevHub.ViewModels.Git;

namespace DevHub.ViewModels.Documents;

/// <summary>
/// A row in a repository tab's sidebar (Fork-style tree): a section (Local Changes, All Commits,
/// Tags…), the Branches / Remotes groups, a remote, a branch folder ("feature"), a branch, or a
/// hint ("No remotes yet"). Children make it foldable.
/// </summary>
public abstract partial class SidebarNode : ObservableObject
{
    public abstract string Title { get; }
    public abstract string IconKind { get; }

    /// <summary>Stable identity across rebuilds (remembers what's unfolded and selected).</summary>
    public abstract string Key { get; }

    public ObservableCollection<SidebarNode> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;

    /// <summary>Shown as a badge when above zero.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount), nameof(AccessibleName))]
    private int _count;

    public bool HasCount => Count > 0;

    public virtual string? ToolTip => null;

    /// <summary>What a screen reader says (the TreeViewItem's AutomationProperties.Name).</summary>
    public string AccessibleName => ToString();

    public override string ToString() => HasCount ? $"{Title}, {Count}" : Title;
}

/// <summary>A sidebar entry that shows a page on the right (Local Changes, All Commits, Tags, Stashes, Submodules).</summary>
public sealed class RepositorySection(string title, string iconKind, string placeholder) : SidebarNode
{
    public override string Title { get; } = title;
    public override string IconKind { get; } = iconKind;
    public override string Key => "section:" + Title;

    /// <summary>Shown for a section that has no page yet.</summary>
    public string Placeholder { get; } = placeholder;
}

/// <summary>"Branches" or "Remotes": folds open to the branches / remotes; its menu acts on the group.</summary>
public sealed class SidebarGroupNode(string title, string iconKind) : SidebarNode
{
    public override string Title { get; } = title;
    public override string IconKind { get; } = iconKind;
    public override string Key => "group:" + Title;

    // The group's menu (null hides an item): Branches → New Branch; Remotes → Manage / Publish / Fetch.
    public ICommand? NewBranchCommand { get; init; }
    public ICommand? ManageRemotesCommand { get; init; }
    public ICommand? PublishCommand { get; init; }

    /// <summary>"Publish to GitHub…" (from the hosting plugin), or null: then Publish isn't offered.</summary>
    public string? PublishText { get; init; }
    public ICommand? FetchCommand { get; init; }

    public bool IsBranches => NewBranchCommand is not null;
    public bool IsRemotes => ManageRemotesCommand is not null;
    public bool CanPublish => IsRemotes && PublishText is not null;
}

/// <summary>A remote ("origin"), holding its fetched branches.</summary>
public sealed class SidebarRemoteNode(GitRemote remote, ICommand manageRemotes, ICommand fetch) : SidebarNode
{
    public GitRemote Remote { get; } = remote;
    public override string Title => Remote.Name;
    public override string IconKind => Controls.IconKind.Fetch;
    public override string Key => "remote:" + Remote.Name;
    public override string? ToolTip => Remote.FetchUrl == Remote.PushUrl
        ? Remote.FetchUrl
        : $"Fetch: {Remote.FetchUrl}\nPush: {Remote.PushUrl}";

    public ICommand ManageRemotesCommand { get; } = manageRemotes;
    public ICommand FetchCommand { get; } = fetch;
}

/// <summary>A branch folder: "feature" for feature/login and feature/search.</summary>
public sealed class SidebarFolderNode(string name, string key) : SidebarNode
{
    public override string Title { get; } = name;
    public override string IconKind => Controls.IconKind.Folder;
    public override string Key { get; } = key;
}

/// <summary>A branch: its last name part ("login" in feature/login), bold when checked out, ↑↓ against its upstream.</summary>
public sealed class SidebarBranchNode : SidebarNode
{
    public SidebarBranchNode(GitBranch branch, string title, BranchOperations operations)
    {
        Branch = branch;
        Title = title;
        var available = operations.IsAvailable;
        CheckoutCommand = new AsyncRelayCommand(() => operations.CheckoutAsync(branch), () => available && !branch.IsCurrent);
        NewBranchFromCommand = new AsyncRelayCommand(() => operations.NewBranchAsync(branch), () => available);
        MergeIntoCurrentCommand = new AsyncRelayCommand(() => operations.MergeIntoCurrentAsync(branch), () => available && !branch.IsCurrent);
        RebaseCurrentOntoCommand = new AsyncRelayCommand(() => operations.RebaseCurrentOntoAsync(branch), () => available && !branch.IsCurrent);
        RenameCommand = new AsyncRelayCommand(() => operations.RenameAsync(branch), () => available && !branch.IsRemote);
        DeleteCommand = new AsyncRelayCommand(() => operations.DeleteAsync(branch),
            () => available && !branch.IsRemote && !branch.IsCurrent);
    }

    public GitBranch Branch { get; }
    public override string Title { get; }
    public override string IconKind => Controls.IconKind.Vcs;
    public override string Key => "branch:" + Branch.Ref;

    public bool IsCurrent => Branch.IsCurrent;
    public bool IsLocal => !Branch.IsRemote;

    /// <summary>"↑1 ↓2" against the upstream, "gone" when it was deleted on the remote, or null.</summary>
    public string? SyncText => Branch.UpstreamGone ? "gone"
        : (Branch.Ahead, Branch.Behind) switch
        {
            (0, 0) => null,
            (var a, 0) => $"↑{a}",
            (0, var b) => $"↓{b}",
            var (a, b) => $"↑{a} ↓{b}",
        };

    public override string? ToolTip =>
        $"{Branch.ShortName}{(Branch.Upstream is { } up ? $" → {up}" : "")}{(IsCurrent ? " (checked out)" : "")}\n" +
        $"{Branch.CommitId[..Math.Min(10, Branch.CommitId.Length)]} {Branch.Subject}\n" +
        (IsCurrent ? "Select to show it in All Commits" : "Double-click to check out · select to show it in All Commits");

    public IAsyncRelayCommand CheckoutCommand { get; }
    public IAsyncRelayCommand NewBranchFromCommand { get; }
    public IAsyncRelayCommand MergeIntoCurrentCommand { get; }
    public IAsyncRelayCommand RebaseCurrentOntoCommand { get; }
    public IAsyncRelayCommand RenameCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }

    public override string ToString() => IsCurrent ? $"{Branch.ShortName} (current)" : Branch.ShortName;
}

/// <summary>A muted line in place of an empty group ("No remotes yet: Manage Remotes…"); double-click acts.</summary>
public sealed class SidebarHintNode(string text, string key, ICommand? command = null) : SidebarNode
{
    public override string Title { get; } = text;
    public override string IconKind => Controls.IconKind.Info;
    public override string Key { get; } = key;
    public ICommand? Command { get; } = command;
}

/// <summary>
/// Fills the Branches and Remotes groups from git (folders from "/" in names, like Fork) and keeps
/// what was unfolded across refreshes. New nodes open by default when they're groups or remotes,
/// or when they lead to the checked-out branch.
/// </summary>
public sealed class SidebarBranchTree(BranchOperations operations)
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    private IReadOnlyList<GitBranch> _lastBranches = [];
    private IReadOnlyList<GitRemote> _lastRemotes = [];

    /// <summary>Rebuilds both groups when the branches or remotes changed; returns whether it did.</summary>
    public bool Update(SidebarGroupNode branchesGroup, SidebarGroupNode remotesGroup, IReadOnlyList<GitBranch> branches,
        IReadOnlyList<GitRemote> remotes, ICommand manageRemotes, ICommand fetch)
    {
        if (branches.SequenceEqual(_lastBranches) && remotes.SequenceEqual(_lastRemotes) && _seen.Count > 0)
            return false;
        _lastBranches = branches;
        _lastRemotes = remotes;

        Remember(branchesGroup);
        Remember(remotesGroup);

        var local = branches.Where(b => !b.IsRemote).ToList();
        Fill(branchesGroup, local, "local", b => b.Name);
        if (local.Count == 0)
            branchesGroup.Children.Add(new SidebarHintNode("No branches yet: make the first commit.", "hint:branches"));
        branchesGroup.Count = local.Count;

        remotesGroup.Children.Clear();
        foreach (var remote in remotes)
        {
            var node = new SidebarRemoteNode(remote, manageRemotes, fetch);
            var ofRemote = branches.Where(b => b.IsRemote && b.Remote == remote.Name).ToList();
            Fill(node, ofRemote, "remote:" + remote.Name, b => b.Name);
            if (ofRemote.Count == 0)
                node.Children.Add(new SidebarHintNode("No branches here: Fetch (double-click), or Push if the remote is new.", "hint:" + node.Key, fetch));
            node.Count = ofRemote.Count;
            Open(node, defaultOpen: true);
            remotesGroup.Children.Add(node);
        }
        if (remotes.Count == 0)
            remotesGroup.Children.Add(new SidebarHintNode("No remotes yet: Manage Remotes… (double-click)", "hint:remotes", manageRemotes));
        remotesGroup.Count = remotes.Count;

        Open(branchesGroup, defaultOpen: true);
        Open(remotesGroup, defaultOpen: true);
        return true;
    }

    /// <summary>All nodes currently in the groups (to find the selected one again after a rebuild).</summary>
    public static IEnumerable<SidebarNode> Descendants(SidebarNode node) =>
        node.Children.SelectMany(c => new[] { c }.Concat(Descendants(c)));

    private void Fill(SidebarNode parent, IReadOnlyList<GitBranch> branches, string scope, Func<GitBranch, string> name)
    {
        parent.Children.Clear();
        Add(parent, PathTree.Build(branches, name));

        void Add(SidebarNode into, IReadOnlyList<PathTreeNode<GitBranch>> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.IsFolder)
                {
                    var folder = new SidebarFolderNode(n.Name, $"folder:{scope}/{n.Path}");
                    Add(folder, n.Children);
                    // A folder holding the checked-out branch opens the first time it's seen.
                    Open(folder, defaultOpen: PathTree.Flatten(n.Children).Any(c => c.Item is { IsCurrent: true }));
                    into.Children.Add(folder);
                }
                else
                {
                    into.Children.Add(new SidebarBranchNode(n.Item!, n.Name, operations));
                }
            }
        }
    }

    /// <summary>Unfolded if it was before; new nodes take <paramref name="defaultOpen"/>.</summary>
    private void Open(SidebarNode node, bool defaultOpen)
    {
        if (_seen.Add(node.Key) && defaultOpen)
            _expanded.Add(node.Key);
        node.IsExpanded = _expanded.Contains(node.Key);
    }

    /// <summary>Records what the user has unfolded / folded before the nodes are replaced.</summary>
    private void Remember(SidebarNode group)
    {
        foreach (var node in new[] { group }.Concat(Descendants(group)))
        {
            if (!_seen.Contains(node.Key))
                continue;
            if (node.IsExpanded)
                _expanded.Add(node.Key);
            else
                _expanded.Remove(node.Key);
        }
    }
}
