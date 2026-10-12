using DevHub.Git.Core;
using DevHub.Services;

namespace DevHub.ViewModels.Documents;

/// <summary>
/// Creates blame tabs from ids like "Blame:|C:\P\DevHub|src/App.cs" (the file on disk) or
/// "Blame:1a2b…|C:\P\DevHub|src/App.cs" (in a commit). "|" can't appear in a Windows path or a
/// commit id, so it separates the parts.
/// </summary>
public sealed class BlameDocumentFactory(RepositoryTabServices services, GitRunner? runner = null) : IDocumentFactory
{
    public string IdPrefix => BlameDocumentViewModel.IdPrefix;

    public static string IdFor(string repositoryPath, string filePath, string? commitId) =>
        $"{BlameDocumentViewModel.IdPrefix}{commitId}|{repositoryPath}|{filePath}";

    public DocumentViewModelBase? Create(string id)
    {
        if (!id.StartsWith(IdPrefix, StringComparison.Ordinal))
            return null;

        var parts = id[IdPrefix.Length..].Split('|');
        if (parts.Length != 3 || parts[2].Length == 0 || !parts[0].All(Uri.IsHexDigit))
            return null;

        var repositoryPath = parts[1];
        // Repository moved or deleted since last time → skip (the DocumentManager tells the user).
        if (!Directory.Exists(repositoryPath))
            return null;

        var client = runner is null ? null : new GitRepositoryClient(runner, repositoryPath);
        return new BlameDocumentViewModel(repositoryPath, parts[2], parts[0].Length == 0 ? null : parts[0], client, services.Editor);
    }
}
