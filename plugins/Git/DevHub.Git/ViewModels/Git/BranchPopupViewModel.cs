using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Git.Core;
using DevHub.Sdk.Core.Search;

namespace DevHub.ViewModels.Git;

/// <summary>
/// JetBrains' branch popup, opened from the title-bar branch widget (or Git → Branches…,
/// Ctrl+Shift+`): type to filter, ↑↓ to move, Enter to check out. "New Branch…" on top, then
/// local, then remote branches.
/// </summary>
public sealed partial class BranchPopupViewModel : ViewModelBase
{
    private readonly BranchOperations _operations;
    private IReadOnlyList<GitBranch> _branches = [];

    public BranchPopupViewModel(BranchOperations operations) => _operations = operations;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private BranchPopupRow? _selected;

    public ObservableCollection<BranchPopupRow> Rows { get; } = new();

    partial void OnQueryChanged(string value) => Rebuild();

    public void Update(IReadOnlyList<GitBranch> branches)
    {
        _branches = branches;
        if (IsOpen)
            Rebuild();
    }

    [RelayCommand]
    public void Open()
    {
        Query = "";
        Rebuild();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void MoveDown() => Move(+1);

    [RelayCommand]
    private void MoveUp() => Move(-1);

    /// <summary>Enter: run the selected row.</summary>
    [RelayCommand]
    private void Execute()
    {
        if (Selected is { } row)
            Run(row);
    }

    private void Run(BranchPopupRow row)
    {
        IsOpen = false;
        if (row.Kind == BranchPopupRowKind.NewBranch)
            _ = _operations.NewBranchAsync();
        else if (row.Branch is { IsCurrent: false } branch)
            _ = _operations.CheckoutAsync(branch);
    }

    private void Move(int direction)
    {
        var selectable = Rows.Where(r => r.IsSelectable).ToList();
        if (selectable.Count == 0)
            return;
        var index = Selected is null ? -1 : selectable.IndexOf(Selected);
        Selected = selectable[Math.Clamp(index + direction, 0, selectable.Count - 1)];
    }

    private void Rebuild()
    {
        var filtering = !string.IsNullOrWhiteSpace(Query);
        Rows.Clear();
        if (!filtering)
            Rows.Add(new BranchPopupRow(BranchPopupRowKind.NewBranch, "New Branch…", null, Run));

        AddGroup("Local", _branches.Where(b => !b.IsRemote));
        AddGroup("Remote", _branches.Where(b => b.IsRemote));

        // First branch that isn't the current one: Enter right away switches somewhere.
        Selected = Rows.FirstOrDefault(r => r.Branch is { IsCurrent: false }) ?? Rows.FirstOrDefault(r => r.IsSelectable);

        void AddGroup(string header, IEnumerable<GitBranch> branches)
        {
            var rows = branches
                .Select(b => (Branch: b, Match: filtering ? FuzzyMatcher.Match(Query, b.ShortName) : null))
                .Where(x => !filtering || x.Match is not null)
                .OrderByDescending(x => x.Match?.Score ?? 0)
                .ThenByDescending(x => x.Branch.IsCurrent)
                .ThenBy(x => x.Branch.ShortName, StringComparer.OrdinalIgnoreCase)
                .Select(x => new BranchPopupRow(BranchPopupRowKind.Branch, x.Branch.ShortName, x.Branch, Run))
                .ToList();
            if (rows.Count == 0)
                return;
            Rows.Add(new BranchPopupRow(BranchPopupRowKind.Header, header, null, Run));
            foreach (var row in rows)
                Rows.Add(row);
        }
    }
}

public enum BranchPopupRowKind
{
    Header,
    NewBranch,
    Branch,
}

/// <summary>A row in the branch popup: a group header, "New Branch…", or a branch.</summary>
public sealed class BranchPopupRow
{
    public BranchPopupRow(BranchPopupRowKind kind, string text, GitBranch? branch, Action<BranchPopupRow> run)
    {
        Kind = kind;
        Text = text;
        Branch = branch;
        RunCommand = new RelayCommand(() => run(this), () => IsSelectable);
    }

    public BranchPopupRowKind Kind { get; }
    public string Text { get; }
    public GitBranch? Branch { get; }

    public bool IsHeader => Kind == BranchPopupRowKind.Header;
    public bool IsSelectable => !IsHeader;
    public bool IsCurrent => Branch?.IsCurrent == true;
    public string IconKind => Kind == BranchPopupRowKind.NewBranch ? Controls.IconKind.Add : Controls.IconKind.Vcs;
    public bool ShowIcon => !IsHeader;

    /// <summary>"↑1 ↓2" for a local branch with an upstream.</summary>
    public string? SyncText => Branch is { } b && (b.Ahead > 0 || b.Behind > 0)
        ? string.Join(' ', new[] { b.Ahead > 0 ? $"↑{b.Ahead}" : null, b.Behind > 0 ? $"↓{b.Behind}" : null }.Where(s => s is not null))
        : null;

    /// <summary>Click on the row.</summary>
    public RelayCommand RunCommand { get; }

    public override string ToString() => IsCurrent ? $"{Text} (current)" : Text;
}
