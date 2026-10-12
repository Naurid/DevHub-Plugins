using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DevHub.ViewModels.Documents;

namespace DevHub.ViewModels.Git;

/// <summary>
/// The Git plugin's idea of "the repository you're working in": the repository tab in front,
/// or, when a diff or blame tab is in front, the open repository tab it belongs to (matched by
/// <see cref="DocumentViewModelBase.ContextPath"/>); else null. Git actions and the title-bar
/// branch widget act on <see cref="Repository"/>. The shell's editor area knows nothing about
/// repositories; this watches it through <see cref="IDocumentService"/>.
/// </summary>
public sealed class ActiveRepositoryService : ObservableObject, IDisposable
{
    private readonly IDocumentService _documents;

    public ActiveRepositoryService(IDocumentService documents)
    {
        _documents = documents;
        // Unsubscribed in Dispose (the DI container disposes singletons at shutdown).
        _documents.PropertyChanged += OnDocumentsChanged;
        // A diff / blame tab's repository tab can open or close while that tab stays in front.
        _documents.OpenDocumentsChanged += OnOpenChanged;
    }

    public RepositoryDocumentViewModel? Repository =>
        _documents.Active as RepositoryDocumentViewModel
        ?? (_documents.Active?.ContextPath is { } path
            ? _documents.OpenDocuments.OfType<RepositoryDocumentViewModel>()
                .FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))
            : null);

    /// <summary>The active repository with its tab brought to the front (for actions that show something in it).</summary>
    public RepositoryDocumentViewModel BringToFront()
    {
        var repository = Repository ?? throw new InvalidOperationException("No active repository.");
        _documents.Active = repository;
        return repository;
    }

    private void OnDocumentsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IDocumentService.Active))
            OnPropertyChanged(nameof(Repository));
    }

    private void OnOpenChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(Repository));

    public void Dispose()
    {
        _documents.PropertyChanged -= OnDocumentsChanged;
        _documents.OpenDocumentsChanged -= OnOpenChanged;
    }
}
