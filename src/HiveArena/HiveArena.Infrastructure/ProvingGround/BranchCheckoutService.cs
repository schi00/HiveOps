using Microsoft.Extensions.Logging;

namespace HiveArena.Infrastructure.ProvingGround;

public sealed class BranchCheckoutService(ILogger<BranchCheckoutService> logger) : IBranchCheckoutService
{
    public async Task<BranchCheckoutResult> CheckoutAsync(
        string? repoUrl,
        string? branchName,
        string? commitSha,
        CancellationToken cancellationToken = default)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "hivearena", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "checkout.txt"),
            $"repo={repoUrl}\nbranch={branchName}\ncommit={commitSha}\n",
            cancellationToken);

        logger.LogInformation(
            "HiveArena branch checkout stub: workspace {Workspace} branch {Branch} commit {Commit}",
            workspace,
            branchName,
            commitSha);

        return new BranchCheckoutResult(true, workspace, "Stub checkout; replace with git shallow clone.");
    }
}
