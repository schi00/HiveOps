namespace HiveOps.Domain.Interfaces;

public interface IGitService
{
    Task<string> CreateBranchAsync(string branchName, CancellationToken ct = default);
    Task<string> CommitAsync(string message, IEnumerable<string> files, CancellationToken ct = default);
    Task PushAsync(string branchName, CancellationToken ct = default);
    Task<string> GetDiffAsync(string branchName, CancellationToken ct = default);
    Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default);

    /// <summary>
    /// Merges a pull request / merge request for the given branch via the provider API.
    /// Returns the merge commit hash.
    /// </summary>
    Task<string> MergePullRequestAsync(string branchName, CancellationToken ct = default);
}
