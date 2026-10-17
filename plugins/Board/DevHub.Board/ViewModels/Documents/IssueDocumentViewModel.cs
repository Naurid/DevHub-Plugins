using CommunityToolkit.Mvvm.ComponentModel;
using DevHub.Board.Core.Storage;
using DevHub.Controls;
using DevHub.Services.Board;
using DevHub.ViewModels.Board;
using DevHub.Sdk.Core.Localization;

namespace DevHub.ViewModels.Documents;

/// <summary>An issue in its own tab ("BoardIssue:folder|C:\…|&lt;issue id&gt;"): the full issue view.</summary>
public sealed partial class IssueDocumentViewModel : DocumentViewModelBase, IDisposable
{
    public const string IdPrefix = "BoardIssue:";

    private readonly BoardUiServices _services;
    private BoardContext? _context;
    private bool _loading;
    private bool _disposed;
    private string _title;

    public IssueDocumentViewModel(BoardLocation location, string issueId, BoardUiServices services)
    {
        Location = location;
        IssueId = issueId;
        _services = services;
        _title = L.T("Issue");
    }

    public BoardLocation Location { get; }
    public string IssueId { get; }

    public override string Id => IssueDocumentFactory.IdFor(Location, IssueId);
    public override string Title => _title;
    public override string Icon => IconKind.Edit;
    public override string? ContextPath => Location.Location;

    [ObservableProperty] private IssueDetailViewModel? _detail;
    [ObservableProperty] private string? _message;

    protected override void OnActivationChanged(bool isActive)
    {
        if (isActive && _context is null && !_loading)
            _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _context = await _services.Boards.AcquireAsync(Location);
            if (_disposed)
            {
                if (_context is not null)
                    _services.Boards.Release(_context);
                return;
            }
            if (_context?.Snapshot.Issue(IssueId) is null)
            {
                Message = _context is null ? L.T("The board isn't there any more.") : L.T("This issue was deleted.");
                return;
            }
            _context.Changed += OnBoardChanged;
            Detail = new IssueDetailViewModel(_context, IssueId, _services, isPanel: false)
            {
                OpenInTab = id => OpenDocument?.Invoke(IssueDocumentFactory.IdFor(Location, id)),
                Navigate = id => OpenDocument?.Invoke(IssueDocumentFactory.IdFor(Location, id)),
                Close = () => CloseCommand?.Execute(null),
            };
            UpdateTitle();
        }
        catch (Exception e) when (e is BoardStoreException or IOException or UnauthorizedAccessException)
        {
            Message = L.T("The board can't be opened: ") + e.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnBoardChanged(object? sender, EventArgs e)
    {
        if (_context?.Snapshot.Issue(IssueId) is null)
        {
            Detail?.Dispose();
            Detail = null;
            Message = L.T("This issue was deleted.");
            return;
        }
        Detail?.Refresh();
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var key = _context?.Snapshot.Issue(IssueId)?.Key ?? L.T("Issue");
        if (key == _title)
            return;
        _title = key;
        OnPropertyChanged(nameof(Title));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Detail?.Dispose();
        if (_context is not null)
        {
            _context.Changed -= OnBoardChanged;
            _services.Boards.Release(_context);
            _context = null;
        }
    }
}

public sealed class IssueDocumentFactory(BoardUiServices services) : IDocumentFactory
{
    public string IdPrefix => IssueDocumentViewModel.IdPrefix;

    /// <summary>The issue's id goes last: locations may contain '|' only as the store separator.</summary>
    public static string IdFor(BoardLocation location, string issueId) => IssueDocumentViewModel.IdPrefix + issueId + "|" + location.Key;

    public DocumentViewModelBase? Create(string id)
    {
        if (!id.StartsWith(IdPrefix, StringComparison.Ordinal))
            return null;
        var rest = id[IdPrefix.Length..];
        var bar = rest.IndexOf('|');
        if (bar <= 0 || BoardLocation.FromKey(rest[(bar + 1)..]) is not { } location || services.Boards.Provider(location.StoreKind) is null)
            return null;
        return new IssueDocumentViewModel(location, rest[..bar], services);
    }
}
