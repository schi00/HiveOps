namespace HiveOps.Domain.Interfaces;

public interface IDeploymentService
{
    Task<string> TriggerDeployAsync(string branchName, string commitHash, CancellationToken ct = default);
    Task<string> GetDeployStatusAsync(string deployId, CancellationToken ct = default);
    Task<bool> IsPipelineHealthyAsync(CancellationToken ct = default);

    /// <summary>
    /// Called by a webhook handler when a merge to main is detected.
    /// Triggers re-test and deploy automatically.
    /// </summary>
    Task<string> OnMergeDeployedAsync(string branchName, string commitHash, CancellationToken ct = default);

    /// <summary>
    /// Opens a pull request for the fix branch via the configured provider
    /// (GitHub / Azure DevOps). Returns the PR URL, or a "local-pr://" pseudo-URL
    /// when no external provider credentials are configured.
    /// </summary>
    Task<string?> CreatePullRequestAsync(Guid tenantId, string branchName, string title, string body, CancellationToken ct = default);
}
