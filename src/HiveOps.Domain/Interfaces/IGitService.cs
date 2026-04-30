namespace HiveOps.Domain.Interfaces;

/// <summary>Git operations scoped to a tenant workspace (self-hosted multi-repo) or the global <c>Git:RepoPath</c> fallback.</summary>
public interface IGitService
{
    Task<string> CreateBranchAsync(Guid tenantId, string branchName, CancellationToken ct = default);
    Task<string> CommitAsync(Guid tenantId, string message, IEnumerable<string> files, CancellationToken ct = default);
    Task PushAsync(Guid tenantId, string branchName, CancellationToken ct = default);
    Task<string> GetDiffAsync(Guid tenantId, string branchName, CancellationToken ct = default);
    Task<bool> BranchExistsAsync(Guid tenantId, string branchName, CancellationToken ct = default);

    /// <summary>
    /// Merges a pull request / merge request for the given branch via the provider API.
    /// Returns the merge commit hash.
    /// </summary>
    Task<string> MergePullRequestAsync(Guid tenantId, string branchName, CancellationToken ct = default);
}
