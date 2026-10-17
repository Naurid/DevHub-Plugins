using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevHub.Board.Core;
using DevHub.Controls;
using DevHub.Sdk.Core.Settings;
using DevHub.Services.Board;
using DevHub.ViewModels.Dialogs.Settings;
using DevHub.ViewModels.ToolWindows;

namespace DevHub.ViewModels.Board;

/// <summary>The Boards tool window: recent boards (double-click opens), New Board, Open Board.</summary>
public sealed partial class BoardsToolWindowViewModel : ToolWindowViewModelBase, IDisposable
{
    private readonly BoardService _boards;
    private readonly BoardOpener _opener;

    public BoardsToolWindowViewModel(BoardService boards, BoardOpener opener)
    {
        _boards = boards;
        _opener = opener;
        _boards.RecentChanged += OnRecentChanged;
        Load();
    }

    public override string Id => "Boards";
    public override string Title => "Boards";
    public override string IconKind => Controls.IconKind.Menu;
    public override ToolWindowSlot Slot => ToolWindowSlot.Left;
    public override int Order => 500;

    public ObservableCollection<RecentBoard> Recent { get; } = [];
    [ObservableProperty] private RecentBoard? _selected;
    public bool HasRecent => Recent.Count > 0;

    private void OnRecentChanged(object? sender, EventArgs e) => Load();

    private void Load()
    {
        Recent.Clear();
        foreach (var board in _boards.RecentBoards)
            Recent.Add(board);
        OnPropertyChanged(nameof(HasRecent));
    }

    [RelayCommand]
    private Task NewBoardAsync() => _opener.NewBoardAsync();

    [RelayCommand]
    private Task OpenBoardAsync() => _opener.OpenBoardAsync();

    [RelayCommand]
    private void Open(RecentBoard? board)
    {
        if ((board ?? Selected) is { } b)
            _opener.OpenRecent(b);
    }

    [RelayCommand]
    private void Forget(RecentBoard? board)
    {
        if ((board ?? Selected) is { } b)
            _boards.Forget(b);
    }

    public void Dispose() => _boards.RecentChanged -= OnRecentChanged;
}

/// <summary>Settings → Board: who you are on boards (name, email) and card density.</summary>
public sealed partial class BoardSettingsPageViewModel : SettingsPageViewModelBase
{
    private readonly ISettingsStore _store;

    public BoardSettingsPageViewModel(ISettingsStore store)
    {
        _store = store;
        var settings = store.LoadSection<BoardSettings>();
        _userName = settings.UserName;
        _userEmail = settings.UserEmail;
        _compactCards = settings.CompactCards;
    }

    public override string Title => "Board";
    public override string Keywords => "board jira issues name email cards planning";
    public override int Order => 600;

    public string DefaultName => Environment.UserName;

    [ObservableProperty] private string _userName;
    [ObservableProperty] private string _userEmail;
    [ObservableProperty] private bool _compactCards;

    partial void OnUserNameChanged(string value) => MarkModified();
    partial void OnUserEmailChanged(string value) => MarkModified();
    partial void OnCompactCardsChanged(bool value) => MarkModified();

    public override void Apply() => _store.UpdateSection<BoardSettings>(s =>
    {
        s.UserName = UserName.Trim();
        s.UserEmail = UserEmail.Trim();
        s.CompactCards = CompactCards;
    });
}
