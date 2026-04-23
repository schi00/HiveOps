using HiveOps.Domain.Interfaces;

namespace HiveOps.Infrastructure.Git;

/// <summary>
/// Stub implementation of IGitService using local git CLI commands.
/// In production, replace with LibGit2Sharp or a proper git abstraction.
/// </summary>
public sealed class LocalGitService : IGitService
{
    private readonly string _repoPath;

    public LocalGitService(string repoPath)
    {
        _repoPath = repoPath;
    }

    public Task<string> CreateBranchAsync(string branchName, CancellationToken ct = default)
    {
        return Task.FromResult($"Branch {branchName} created (stub).");
    }

    public Task<string> CommitAsync(string message, IEnumerable<string> files, CancellationToken ct = default)
    {
        return Task.FromResult($"Commit '{message}' created (stub).");
    }

    public Task PushAsync(string branchName, CancellationToken ct = default)
    {
        return Task.FromResult($"Pushed {branchName} (stub).");
    }

    public Task<string> GetDiffAsync(string branchName, CancellationToken ct = default)
    {
        return Task.FromResult("Diff not available in stub mode.");
    }

    public Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    public Task<string> MergePullRequestAsync(string branchName, CancellationToken ct = default)
    {
        // Stub: in production this calls the Git provider API (GitHub/Azure DevOps)
        return Task.FromResult($"MERGED:{branchName}:{DateTimeOffset.UtcNow:O}");
    }
}
