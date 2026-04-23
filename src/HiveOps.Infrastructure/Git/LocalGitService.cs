using System.Diagnostics;
using HiveOps.Domain.Interfaces;
using Microsoft.Extensions.Configuration;

namespace HiveOps.Infrastructure.Git;

/// <summary>
/// Real implementation of IGitService using local git CLI commands.
/// Requires git to be installed on the host and a valid repo path configured.
/// </summary>
public sealed class LocalGitService : IGitService
{
    private readonly string _repoPath;

    public LocalGitService(IConfiguration configuration)
    {
        _repoPath = configuration["Git:RepoPath"] ?? throw new InvalidOperationException("Configuration 'Git:RepoPath' is required.");
    }

    public async Task<string> CreateBranchAsync(string branchName, CancellationToken ct = default)
    {
        var result = await RunGitAsync($"checkout -b {branchName}", ct);
        return result.Trim();
    }

    public async Task<string> CommitAsync(string message, IEnumerable<string> files, CancellationToken ct = default)
    {
        var fileList = string.Join(" ", files.Select(f => $"\"{f}\""));
        await RunGitAsync($"add {fileList}", ct);
        var result = await RunGitAsync($"commit -m \"{message.Replace("\"", "\\\"")}\"", ct);
        return result.Trim();
    }

    public async Task PushAsync(string branchName, CancellationToken ct = default)
    {
        await RunGitAsync($"push -u origin {branchName}", ct);
    }

    public async Task<string> GetDiffAsync(string branchName, CancellationToken ct = default)
    {
        return await RunGitAsync($"diff origin/main...{branchName}", ct);
    }

    public async Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default)
    {
        try
        {
            var output = await RunGitAsync($"branch --list {branchName}", ct);
            return !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> MergePullRequestAsync(string branchName, CancellationToken ct = default)
    {
        await RunGitAsync("checkout main", ct);
        var result = await RunGitAsync($"merge --no-ff {branchName} -m \"Merge {branchName}\"", ct);
        await RunGitAsync("push origin main", ct);
        return $"MERGED:{branchName}:{DateTimeOffset.UtcNow:O}";
    }

    private async Task<string> RunGitAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git process.");
        await process.WaitForExitAsync(ct);

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Git command failed: {error.Trim()}");

        return output;
    }
}
