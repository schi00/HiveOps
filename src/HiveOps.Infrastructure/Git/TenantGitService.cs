using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;

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

    /// <summary>Matches embedded credentials (<c>scheme://userinfo@</c>) so they can be masked in logs/errors.</summary>
    private static readonly Regex UrlCredentialsPattern = new(
        @"://[^/\s]+@",
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

    public Task<string> GetWorkspacePathAsync(Guid tenantId, CancellationToken ct = default) =>
        ResolveRepositoryPathAsync(tenantId, ct);

    /// <summary>
    /// Connectivity probe for the tenant-facing deploy-git settings screen: runs
    /// <c>git ls-remote --heads &lt;url&gt; &lt;branch&gt;</c> with the tenant's stored
    /// access token injected (https URLs only). Never exposes the token in the
    /// returned message.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestRepositoryAccessAsync(Guid tenantId, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var configs = scope.ServiceProvider.GetRequiredService<ITenantConfigService>();
            var cfg = await configs.GetConfigurationAsync(tenantId, ct);

            var url = cfg.DeployGit.GitRepositoryUrl?.Trim();
            if (string.IsNullOrWhiteSpace(url))
                return (false, "No hay un repositorio Git configurado para este tenant.");

            var branch = string.IsNullOrWhiteSpace(cfg.DeployGit.GitDefaultBranch)
                ? "main"
                : cfg.DeployGit.GitDefaultBranch.Trim();

            var remoteUrl = await BuildAuthenticatedRemoteUrlAsync(configs, tenantId, url, ct);

            var workingDir = _configuration["Git:WorkspacesRoot"];
            if (string.IsNullOrWhiteSpace(workingDir))
                workingDir = Path.GetTempPath();
            Directory.CreateDirectory(workingDir);

            await RunGitAsync(workingDir, ["ls-remote", "--heads", remoteUrl, branch], ct);
            return (true, "Conexión OK — repo accesible");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "git ls-remote connectivity test failed for tenant {TenantId}", tenantId);
            var detail = ex.Message.Trim();
            if (detail.Length > 300)
                detail = detail[..300] + "...";
            return (false, $"No se pudo acceder al repositorio: {detail}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Deploy-git connectivity test failed for tenant {TenantId}", tenantId);
            return (false, "No se pudo ejecutar la prueba de conexión Git.");
        }
    }

    public async Task<IReadOnlyList<GitCommitEvidence>> GetRecentCommitsForFilesAsync(
        Guid tenantId, IReadOnlyList<string> relativePaths, int maxCount = 5, CancellationToken ct = default)
    {
        if (relativePaths.Count == 0)
            return [];

        string repoPath;
        try
        {
            repoPath = await ResolveRepositoryPathAsync(tenantId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workspace unavailable for tenant {TenantId}; returning empty commit evidence.", tenantId);
            return [];
        }

        var validPaths = relativePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace('\\', '/'))
            .Where(p => ValidFilePathPattern.IsMatch(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        if (validPaths.Count == 0)
            return [];

        var args = new List<string>
        {
            "log",
            $"-n{Math.Clamp(maxCount, 1, 50)}",
            "--date=iso-strict",
            "--name-only",
            "--pretty=format:--commit--%H|%an|%ad|%s",
            "--"
        };
        args.AddRange(validPaths);

        string output;
        try
        {
            output = await RunGitAsync(repoPath, args.ToArray(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "git log failed in workspace {Workspace} for tenant {TenantId}", repoPath, tenantId);
            return [];
        }

        var commits = new List<GitCommitEvidence>();
        var lines = output.Replace("\r\n", "\n").Split('\n');
        string? hash = null, author = null, subject = null;
        var committedAt = DateTimeOffset.MinValue;
        var touched = new List<string>();

        void Flush()
        {
            if (hash is null)
                return;
            commits.Add(new GitCommitEvidence(hash, author ?? string.Empty, committedAt, subject ?? string.Empty, touched.ToArray()));
            touched.Clear();
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("--commit--", StringComparison.Ordinal))
            {
                Flush();
                var parts = line["--commit--".Length..].Split('|', 4);
                if (parts.Length == 4)
                {
                    hash = parts[0];
                    author = parts[1];
                    committedAt = DateTimeOffset.TryParse(parts[2], out var dt) ? dt : DateTimeOffset.MinValue;
                    subject = parts[3];
                }
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line) && hash is not null)
                touched.Add(line.Trim());
        }

        Flush();
        return commits;
    }

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

        // Inject the tenant's stored access token (https only; ssh/file URLs pass through).
        var remoteUrl = await BuildAuthenticatedRemoteUrlAsync(configs, tenantId, url, ct);

        Directory.CreateDirectory(workspacesRoot);
        var localPath = Path.Combine(workspacesRoot, tenantId.ToString("N"));

        if (!Directory.Exists(Path.Combine(localPath, ".git")))
        {
            if (Directory.Exists(localPath))
                Directory.Delete(localPath, recursive: true);

            await RunGitCloneAsync(workspacesRoot, remoteUrl, tenantId.ToString("N"), branch, ct);
        }
        else
        {
            // Refresh origin with the current authenticated URL so fetches/pushes keep
            // working after token rotation (token lives only in .git/config remote URL).
            await RunGitAsync(localPath, ["remote", "set-url", "origin", remoteUrl], ct);
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

    /// <summary>
    /// Builds the remote URL with the tenant's deploy git token injected as
    /// <c>x-access-token</c> credentials (GitHub convention). Only http(s) URLs get
    /// the token — ssh and file paths pass through untouched.
    /// </summary>
    private static async Task<string> BuildAuthenticatedRemoteUrlAsync(
        ITenantConfigService configs, Guid tenantId, string url, CancellationToken ct)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;

        var token = await configs.GetDeployGitTokenAsync(tenantId, ct);
        if (string.IsNullOrEmpty(token))
            return url;

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            return url;

        var authorityStart = schemeEnd + 3;
        var pathStart = url.IndexOf('/', authorityStart);
        var authority = pathStart < 0 ? url[authorityStart..] : url[authorityStart..pathStart];
        var rest = pathStart < 0 ? string.Empty : url[pathStart..];

        // Replace any pre-existing userinfo so we never produce a malformed URL.
        var atIndex = authority.LastIndexOf('@');
        var hostPort = atIndex >= 0 ? authority[(atIndex + 1)..] : authority;

        return $"{url[..authorityStart]}x-access-token:{Uri.EscapeDataString(token)}@{hostPort}{rest}";
    }

    /// <summary>Masks <c>scheme://userinfo@</c> credentials so tokens never reach logs or error messages.</summary>
    private static string MaskUrlCredentials(string text)
        => UrlCredentialsPattern.Replace(text, "://***@");

    private async Task RunGitCloneAsync(string workspacesRoot, string url, string folderName, string branch, CancellationToken ct)
    {
        try
        {
            await RunGitAsync(workspacesRoot, ["clone", "--branch", branch, "--single-branch", url, folderName], ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Branch-aware clone failed; falling back to full clone for {Url}", MaskUrlCredentials(url));
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
            throw new InvalidOperationException($"Git command failed: {MaskUrlCredentials(error.Trim())}");

        return output;
    }
}
