namespace HiveArena.Infrastructure.ProvingGround;

public sealed record BranchCheckoutResult(bool Success, string WorkspacePath, string? Message);

public interface IBranchCheckoutService
{
    /// <summary>
    /// MVP: records intent and returns a temp workspace folder (no git binary required).
    /// Replace with shallow clone of <paramref name="repoUrl"/> at <paramref name="branchName"/> / <paramref name="commitSha"/>.
    /// </summary>
    Task<BranchCheckoutResult> CheckoutAsync(
        string? repoUrl,
        string? branchName,
        string? commitSha,
        CancellationToken cancellationToken = default);
}
