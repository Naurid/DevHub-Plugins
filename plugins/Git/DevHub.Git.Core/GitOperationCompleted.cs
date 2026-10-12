namespace DevHub.Git.Core;

/// <summary>What <see cref="GitRepositoryClient.OperationCompleted"/> reports.</summary>
public enum GitOperation
{
    Committed,
    Fetched,
    Pulled,
    Pushed,
}

/// <param name="CommitId">The new commit (for <see cref="GitOperation.Committed"/>), else null.</param>
public sealed record GitOperationCompleted(GitOperation Operation, string? CommitId);
