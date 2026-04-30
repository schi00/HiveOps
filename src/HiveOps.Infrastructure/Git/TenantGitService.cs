using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Domain.Interfaces;

namespace HiveOps.Infrastructure.Git;

/// <summary>
/// Git CLI integration: uses <c>Git:RepoPath</c> when not self-hosted or tenant has no repo URL;
/// otherwise clones under <c>Git:WorkspacesRoot/{tenantId:N}</c> using <see cref="DeployGitConfig.GitRepositoryUrl"/>.
/// </summary>
public sealed class TenantGitService : IGitService
{
    private static readonly Regex ValidBranchNamePattern = new(
        @"^[a-zA-Z0-9][a-zA-Z0-9._\-/]*$",
        RegexOptions.Compiled);

    private static readonly Regex ValidFilePathPattern = new(
        @"^[a-zA-Z0-9._\-/]+$",
        RegexOptions.Compiled);

    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<HiveOpsDeploymentOptions> _deploymentOptions;
    private readonly ILogger<TenantGitService> _logger;

    private readonly string? _fallbackRepoPath;

    public TenantGitService(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        IOptions<HiveOpsDeploymentOptions> deploymentOptions,
        ILogger<TenantGitService> logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _deploymentOptions = deploymentOptions;
        _logger = logger;
        _fallbackRepoPath = configuration["Git:RepoPath"];
    }

    public Task<string> CreateBranchAsync(Guid tenantId, string branchName, CancellationToken ct = default) =>
        RunWithRepoAsync(tenantId, async repoPath =>
        {
            ValidateBranchName(branchName);
            var result = await RunGitAsync(repoPath, ["checkout", "-b", branchName], ct);
            return result.Trim();
        }, ct);

    public Task<string> CommitAsync(Guid tenantId, string message, IEnumerable<string> files, CancellationToken ct = default) =>
        RunWithRepoAsync(tenantId, async repoPath =>
        {
            var fileList = files.ToList();
            foreach (var file in fileList)
                ValidateFilePath(file);

            if (!fileList.Any())
                throw new ArgumentException("At least one file is required for commit.");

            foreach (var file in fileList)
                await RunGitAsync(repoPath, ["add", file], ct);

            var sanitizedMessage = SanitizeCommitMessage(message);
            var result = await RunGitAsync(repoPath, ["commit", "-m", sanitizedMessage], ct);
            return result.Trim();
        }, ct);

    public Task PushAsync(Guid tenantId, string branchName, CancellationToken ct = default) =>
        RunWithRepoAsync(tenantId, async repoPath =>
        {
            ValidateBranchName(branchName);
            await RunGitAsync(repoPath, ["push", "-u", "origin", branchName], ct);
        }, ct);

    public Task<string> GetDiffAsync(Guid tenantId, string branchName, CancellationToken ct = default) =>
        RunWithRepoAsync(tenantId, async repoPath =>
        {
            ValidateBranchName(branchName);
            var defaultBranch = await GetDefaultBranchNameAsync(tenantId, ct);
            return await RunGitAsync(repoPath, ["diff", $"origin/{defaultBranch}...{branchName}"], ct);
        }, ct);

    public Task<bool> BranchExistsAsync(Guid tenantId, string branchName, CancellationToken ct = default) =>
        RunWithRepoAsync(tenantId, async repoPath =>
        {
            ValidateBranchName(branchName);
            try
            {
                var output = await RunGitAsync(repoPath, ["branch", "--list", branchName], ct);
                return !string.IsNullOrWhiteSpace(output);
            }
            catch
            {
                return false;
            }
        }, ct);

    public Task<string> MergePullRequestAsync(Guid tenantId, string branchName, CancellationToken ct = default) =>
        RunWithRepoAsync(tenantId, async repoPath =>
        {
            ValidateBranchName(branchName);
            var defaultBranch = await GetDefaultBranchNameAsync(tenantId, ct);
            await RunGitAsync(repoPath, ["checkout", defaultBranch], ct);
            var mergeMessage = $"Merge {branchName}";
            var result = await RunGitAsync(repoPath, ["merge", "--no-ff", branchName, "-m", mergeMessage], ct);
            await RunGitAsync(repoPath, ["push", "origin", defaultBranch], ct);
            return $"MERGED:{branchName}:{DateTimeOffset.UtcNow:O}";
        }, ct);

    private async Task<string> GetDefaultBranchNameAsync(Guid tenantId, CancellationToken ct)
    {
        if (_deploymentOptions.Value.Mode != HiveOpsDeploymentMode.SelfHosted || tenantId == Guid.Empty)
            return "main";

        using var scope = _scopeFactory.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<ITenantConfigService>();
        var cfg = await configs.GetConfigurationAsync(tenantId, ct);
        var b = cfg.DeployGit.GitDefaultBranch?.Trim();
        return string.IsNullOrWhiteSpace(b) ? "main" : b;
    }

    private async Task RunWithRepoAsync(Guid tenantId, Func<string, Task> action, CancellationToken ct)
    {
        var path = await ResolveRepositoryPathAsync(tenantId, ct);
        await action(path);
    }

    private async Task<T> RunWithRepoAsync<T>(Guid tenantId, Func<string, Task<T>> action, CancellationToken ct)
    {
        var path = await ResolveRepositoryPathAsync(tenantId, ct);
        return await action(path);
    }

    private async Task<string> ResolveRepositoryPathAsync(Guid tenantId, CancellationToken ct)
    {
        if (_deploymentOptions.Value.Mode != HiveOpsDeploymentMode.SelfHosted || tenantId == Guid.Empty)
        {
            if (string.IsNullOrWhiteSpace(_fallbackRepoPath))
                throw new InvalidOperationException("Configuration 'Git:RepoPath' is required for this deployment mode.");
            return _fallbackRepoPath;
        }

        using var scope = _scopeFactory.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<ITenantConfigService>();
        var cfg = await configs.GetConfigurationAsync(tenantId, ct);
        var url = cfg.DeployGit.GitRepositoryUrl?.Trim();

        if (string.IsNullOrWhiteSpace(url))
        {
            if (string.IsNullOrWhiteSpace(_fallbackRepoPath))
                throw new InvalidOperationException(
                    "Configure Git:RepoPath or set DeployGit.GitRepositoryUrl for this tenant in self-hosted mode.");
            return _fallbackRepoPath;
        }

        var workspacesRoot = _configuration["Git:WorkspacesRoot"];
        if (string.IsNullOrWhiteSpace(workspacesRoot))
            throw new InvalidOperationException(
                "Configuration 'Git:WorkspacesRoot' is required when using per-tenant GitRepositoryUrl in self-hosted mode.");

        var branch = string.IsNullOrWhiteSpace(cfg.DeployGit.GitDefaultBranch)
            ? "main"
            : cfg.DeployGit.GitDefaultBranch.Trim();

        Directory.CreateDirectory(workspacesRoot);
        var localPath = Path.Combine(workspacesRoot, tenantId.ToString("N"));

        if (!Directory.Exists(Path.Combine(localPath, ".git")))
        {
            if (Directory.Exists(localPath))
                Directory.Delete(localPath, recursive: true);

            await RunGitCloneAsync(workspacesRoot, url, tenantId.ToString("N"), branch, ct);
        }
        else
        {
            await RunGitAsync(localPath, ["fetch", "origin"], ct);
            try
            {
                await RunGitAsync(localPath, ["checkout", branch], ct);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Could not checkout default branch {Branch} for tenant {TenantId}", branch, tenantId);
            }
        }

        return localPath;
    }

    private async Task RunGitCloneAsync(string workspacesRoot, string url, string folderName, string branch, CancellationToken ct)
    {
        try
        {
            await RunGitAsync(workspacesRoot, ["clone", "--branch", branch, "--single-branch", url, folderName], ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Branch-aware clone failed; falling back to full clone for {Url}", url);
            if (Directory.Exists(Path.Combine(workspacesRoot, folderName)))
                Directory.Delete(Path.Combine(workspacesRoot, folderName), recursive: true);

            await RunGitAsync(workspacesRoot, ["clone", url, folderName], ct);
            var localPath = Path.Combine(workspacesRoot, folderName);
            await RunGitAsync(localPath, ["checkout", branch], ct);
        }
    }

    private static void ValidateBranchName(string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            throw new ArgumentException("Branch name cannot be empty.", nameof(branchName));

        if (!ValidBranchNamePattern.IsMatch(branchName))
            throw new ArgumentException($"Invalid branch name: '{branchName}'. Branch names must be alphanumeric with hyphens, underscores, dots, or forward slashes only.", nameof(branchName));

        if (branchName.Contains("..") || branchName.Contains("//"))
            throw new ArgumentException($"Invalid branch name: '{branchName}'. Branch name contains unsafe characters.", nameof(branchName));
    }

    private static void ValidateFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));

        var normalized = filePath.Replace('\\', '/');

        if (normalized.StartsWith("/") || normalized.StartsWith("."))
            throw new ArgumentException($"Invalid file path: '{filePath}'. Path must be relative and cannot start with dot or slash.", nameof(filePath));

        if (normalized.Contains("../") || normalized.Contains("/.."))
            throw new ArgumentException($"Invalid file path: '{filePath}'. Path traversal is not allowed.", nameof(filePath));

        if (!ValidFilePathPattern.IsMatch(filePath))
            throw new ArgumentException($"Invalid file path: '{filePath}'. File paths must be alphanumeric with hyphens, underscores, dots, or forward slashes only.", nameof(filePath));
    }

    private static string SanitizeCommitMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "No message";

        var sanitized = new string(message.Where(c => c >= 32 && c < 127).ToArray());

        if (sanitized.Length > 500)
            sanitized = sanitized[..500] + "...";

        return sanitized;
    }

    private static async Task<string> RunGitAsync(string workingDirectory, string[] arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git process.");
        await process.WaitForExitAsync(ct);

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Git command failed: {error.Trim()}");

        return output;
    }
}
