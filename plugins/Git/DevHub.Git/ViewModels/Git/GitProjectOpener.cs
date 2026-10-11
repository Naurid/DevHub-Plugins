using DevHub.Integration;
using DevHub.ViewModels.Documents;

namespace DevHub.ViewModels.Git;

/// <summary>The Git plugin's <see cref="IProjectOpener"/>: a project folder opens as its repository tab.</summary>
public sealed class GitProjectOpener(IDocumentService documents) : IProjectOpener
{
    public bool Open(string folder) => documents.OpenById(RepositoryDocumentFactory.IdFor(folder));
}
