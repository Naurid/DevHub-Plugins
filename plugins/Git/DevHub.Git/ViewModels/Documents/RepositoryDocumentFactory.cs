using DevHub.Git.Core;
using DevHub.Services;
using DevHub.ViewModels.Git;

namespace DevHub.ViewModels.Documents;

/// <summary>
/// Creates repository tabs from ids like "Repository:C:\P\DevHub" (opening from the
/// Projects tool window, and restoring tabs at startup).
/// </summary>
/// <param name="runner">
/// Shared git runner; null when git isn't installed (tabs then explain that). The default value
/// is what lets DI construct this without a registered GitRunner.
/// </param>
/// <param name="layout">Panel widths and view choices, shared by every repository tab.</param>
public sealed class RepositoryDocumentFactory(RepositoryTabServices services, RepositoryTabLayout layout, GitRunner? runner = null)
    : IDocumentFactory
{
    public string IdPrefix => RepositoryDocumentViewModel.IdPrefix;

    /// <summary>The document id for a repository folder (normalised, so one tab per repository).</summary>
    public static string IdFor(string repositoryPath) =>
        RepositoryDocumentViewModel.IdPrefix + Path.GetFullPath(repositoryPath).TrimEnd('\\', '/');

    public DocumentViewModelBase? Create(string id)
    {
        if (!id.StartsWith(IdPrefix, StringComparison.Ordinal))
            return null;

        var path = id[IdPrefix.Length..];
        // Moved or deleted since last time → skip (the DocumentManager tells the user).
        if (!Directory.Exists(path))
            return null;

        var client = runner is null ? null : new GitRepositoryClient(runner, path);
        return new RepositoryDocumentViewModel(path, client, layout, services);
    }
}
