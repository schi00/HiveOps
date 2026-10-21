namespace HiveArena.Infrastructure.ProvingGround;

public sealed record BranchCheckoutResult(bool Success, string WorkspacePath, string? Message);

public interface IBranchCheckoutService
{
    /// <summary>
    /// Performs a real git clone of <paramref name="repoUrl"/> at <paramref name="branchName"/>
    /// (or a shallow fetch + checkout of <paramref name="commitSha"/>) into a workspace
    /// under <paramref name="workspaceRoot"/> (or a fresh temp dir when null).
    /// Requires the <c>git</c> CLI on PATH.
    /// </summary>
    Task<BranchCheckoutResult> CheckoutAsync(
        string? repoUrl,
        string? branchName,
        string? commitSha,
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default);
}
