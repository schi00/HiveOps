using System.Diagnostics;
using System.Text.RegularExpressions;
using HiveOps.Domain.Interfaces;
using Microsoft.Extensions.Configuration;

namespace HiveOps.Infrastructure.Git;

/// <summary>
/// Real implementation of IGitService using local git CLI commands.
/// Requires git to be installed on the host and a valid repo path configured.
/// All inputs are validated to prevent command injection attacks.
/// </summary>
public sealed class LocalGitService : IGitService
{
    private readonly string _repoPath;

    // Valid git branch name pattern: alphanumeric, hyphens, underscores, dots, forward slashes
    // Cannot start with hyphen, cannot contain consecutive dots, cannot end with slash
    private static readonly Regex ValidBranchNamePattern = new(
        @"^[a-zA-Z0-9][a-zA-Z0-9._\-/]*$",
        RegexOptions.Compiled);

    // Valid file path pattern: alphanumeric, hyphens, underscores, dots, forward slashes
    // Must be relative path within repo, no parent directory traversal
    private static readonly Regex ValidFilePathPattern = new(
        @"^[a-zA-Z0-9._\-/]+$",
        RegexOptions.Compiled);

    public LocalGitService(IConfiguration configuration)
    {
        _repoPath = configuration["Git:RepoPath"] ?? throw new InvalidOperationException("Configuration 'Git:RepoPath' is required.");
    }

    public async Task<string> CreateBranchAsync(string branchName, CancellationToken ct = default)
    {
        ValidateBranchName(branchName);
        var result = await RunGitAsync(["checkout", "-b", branchName], ct);
        return result.Trim();
    }

    public async Task<string> CommitAsync(string message, IEnumerable<string> files, CancellationToken ct = default)
    {
        // Validate all file paths to prevent path traversal
        var fileList = files.ToList();
        foreach (var file in fileList)
        {
            ValidateFilePath(file);
        }

        if (!fileList.Any())
            throw new ArgumentException("At least one file is required for commit.");

        // Add files one by one to avoid shell injection
        foreach (var file in fileList)
        {
            await RunGitAsync(["add", file], ct);
        }

        // Sanitize commit message - replace dangerous characters
        var sanitizedMessage = SanitizeCommitMessage(message);
        var result = await RunGitAsync(["commit", "-m", sanitizedMessage], ct);
        return result.Trim();
    }

    public async Task PushAsync(string branchName, CancellationToken ct = default)
    {
        ValidateBranchName(branchName);
        await RunGitAsync(["push", "-u", "origin", branchName], ct);
    }

    public async Task<string> GetDiffAsync(string branchName, CancellationToken ct = default)
    {
        ValidateBranchName(branchName);
        return await RunGitAsync(["diff", $"origin/main...{branchName}"], ct);
    }

    public async Task<bool> BranchExistsAsync(string branchName, CancellationToken ct = default)
    {
        ValidateBranchName(branchName);
        try
        {
            var output = await RunGitAsync(["branch", "--list", branchName], ct);
            return !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> MergePullRequestAsync(string branchName, CancellationToken ct = default)
    {
        ValidateBranchName(branchName);
        await RunGitAsync(["checkout", "main"], ct);
        var mergeMessage = $"Merge {branchName}";
        var result = await RunGitAsync(["merge", "--no-ff", branchName, "-m", mergeMessage], ct);
        await RunGitAsync(["push", "origin", "main"], ct);
        return $"MERGED:{branchName}:{DateTimeOffset.UtcNow:O}";
    }

    private static void ValidateBranchName(string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            throw new ArgumentException("Branch name cannot be empty.", nameof(branchName));

        if (!ValidBranchNamePattern.IsMatch(branchName))
            throw new ArgumentException($"Invalid branch name: '{branchName}'. Branch names must be alphanumeric with hyphens, underscores, dots, or forward slashes only.", nameof(branchName));

        // Additional security checks
        if (branchName.Contains("..") || branchName.Contains("//"))
            throw new ArgumentException($"Invalid branch name: '{branchName}'. Branch name contains unsafe characters.", nameof(branchName));
    }

    private static void ValidateFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));

        // Normalize and check for path traversal attempts
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

        // Remove null bytes and control characters
        var sanitized = new string(message.Where(c => c >= 32 && c < 127).ToArray());

        // Limit length
        if (sanitized.Length > 500)
            sanitized = sanitized[..500] + "...";

        return sanitized;
    }

    private async Task<string> RunGitAsync(string[] arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Add arguments safely (no shell interpretation)
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git process.");
        await process.WaitForExitAsync(ct);

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Git command failed: {error.Trim()}");

        return output;
    }
}
