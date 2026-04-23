using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Agents.Support;

/// <summary>
/// SelfSupportPlugin — allows the bot to inspect its own codebase and database.
/// Phase 1: supports self-healing of HiveOps itself.
/// </summary>
public sealed class SelfSupportPlugin
{
    private readonly AppDbContext _db;
    private readonly IConversationStateManager _stateManager;

    public SelfSupportPlugin(AppDbContext db, IConversationStateManager stateManager)
    {
        _db = db;
        _stateManager = stateManager;
    }

    // Valid file path pattern: alphanumeric, hyphens, underscores, dots, forward slashes
    // Must stay within src directory, no parent directory traversal
    private static readonly Regex ValidSourcePathPattern = new(
        @"^[a-zA-Z0-9][a-zA-Z0-9._\-/]*\.(cs|json|md|txt|yml|yaml)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [KernelFunction("read_own_source")]
    [Description("Reads a source file from the HiveOps repository to inspect code for diagnostics.")]
    public async Task<string> ReadOwnSourceAsync(
        Kernel kernel,
        [Description("Relative file path within the repo (e.g., src/HiveOps.Agents/Support/SupportPlugin.cs).")] string filePath,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        var repoRoot = FindRepoRoot();

        // Validate file path to prevent path traversal attacks
        if (!IsValidSourceFilePath(filePath))
            return "Invalid file path. Only source files within the repository are allowed.";

        var fullPath = Path.Combine(repoRoot, filePath.Replace('/', Path.DirectorySeparatorChar));
        fullPath = Path.GetFullPath(fullPath);

        // Ensure the resolved path is still within the repo root
        var repoRootFull = Path.GetFullPath(repoRoot);
        if (!fullPath.StartsWith(repoRootFull, StringComparison.OrdinalIgnoreCase))
            return "Access denied: Path is outside the repository.";

        // Only allow reading from specific safe directories
        var allowedDirs = new[] { "src", "tests", "db", ".github" };
        var relativeToRepo = fullPath[(repoRootFull.Length + 1)..];
        var topLevelDir = relativeToRepo.Split(Path.DirectorySeparatorChar, '/')[0];
        if (!allowedDirs.Contains(topLevelDir, StringComparer.OrdinalIgnoreCase))
            return $"Access denied: Can only read from {string.Join(", ", allowedDirs)} directories.";

        if (!File.Exists(fullPath))
            return $"File not found: {filePath}";

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);

        // Store as diagnostic log for the active incident if any
        await LogDiagnosticAsync(tenantId, "read_own_source", $"Read {filePath} ({content.Length} chars)", true, cancellationToken);

        // Return a truncated preview
        var preview = content.Length > 2000 ? content[..2000] + "\n... (truncated)" : content;
        return $"```csharp\n{preview}\n```";
    }

    private static bool IsValidSourceFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        // Normalize path separators
        var normalized = filePath.Replace('\\', '/');

        // Check for path traversal attempts
        if (normalized.Contains("../") || normalized.Contains("/..") || normalized.StartsWith("..") || normalized.StartsWith("/"))
            return false;

        // Must match allowed pattern
        return ValidSourcePathPattern.IsMatch(filePath);
    }

    [KernelFunction("search_own_source")]
    [Description("Searches the HiveOps codebase for files matching a pattern or containing text.")]
    public Task<string> SearchOwnSourceAsync(
        Kernel kernel,
        [Description("Glob pattern or search term (alphanumeric only, max 50 chars).")] string pattern,
        [Description("File extension filter (e.g., .cs).")] string? extension = ".cs",
        CancellationToken cancellationToken = default)
    {
        var repoRoot = FindRepoRoot();
        var srcPath = Path.Combine(repoRoot, "src");
        if (!Directory.Exists(srcPath))
            return Task.FromResult("Source directory not found.");

        // Sanitize pattern to prevent regex injection
        var sanitizedPattern = SanitizeSearchPattern(pattern);
        if (string.IsNullOrEmpty(sanitizedPattern))
            return Task.FromResult("Invalid search pattern. Only alphanumeric characters, hyphens, and underscores are allowed.");

        // Validate extension
        var validExtensions = new[] { ".cs", ".json", ".md", ".txt", ".yml", ".yaml", ".xml", ".config" };
        if (!validExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return Task.FromResult($"Invalid extension. Allowed: {string.Join(", ", validExtensions)}");

        var files = Directory.EnumerateFiles(srcPath, $"*{extension}", SearchOption.AllDirectories)
            .Where(f => f.Contains(sanitizedPattern, StringComparison.OrdinalIgnoreCase) ||
                        File.ReadAllText(f).Contains(sanitizedPattern, StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .Select(f => f.Replace(repoRoot + Path.DirectorySeparatorChar, "").Replace('\\', '/'));

        return Task.FromResult($"Found matches:\n{string.Join("\n", files)}");
    }

    private static string SanitizeSearchPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return string.Empty;

        // Limit length
        if (pattern.Length > 50)
            pattern = pattern[..50];

        // Only allow alphanumeric, hyphens, underscores
        var allowed = pattern.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
        return new string(allowed);
    }

    [KernelFunction("check_own_db_health")]
    [Description("Runs basic health checks on the bot's own database tables.")]
    public async Task<string> CheckOwnDbHealthAsync(
        Kernel kernel,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        var results = new List<string>();

        try
        {
            var convCount = await _db.Conversations.CountAsync(cancellationToken);
            var msgCount = await _db.ConversationMessages.CountAsync(cancellationToken);
            var orphanMsgs = await _db.ConversationMessages
                .CountAsync(m => !_db.Conversations.Any(c => c.Id == m.ConversationId), cancellationToken);

            results.Add($"Conversations: {convCount}");
            results.Add($"Messages: {msgCount}");
            results.Add($"Orphan messages: {orphanMsgs}");

            var tenantsWithoutConfig = await _db.Tenants
                .CountAsync(t => !_db.BusinessConfigs.Any(b => b.TenantId == t.Id), cancellationToken);
            results.Add($"Tenants without BusinessConfig: {tenantsWithoutConfig}");

            await LogDiagnosticAsync(tenantId, "check_own_db_health", JsonSerializer.Serialize(results), true, cancellationToken);
        }
        catch (Exception ex)
        {
            await LogDiagnosticAsync(tenantId, "check_own_db_health", ex.Message, false, cancellationToken);
            return $"DB health check failed: {ex.Message}";
        }

        return $"DB Health:\n{string.Join("\n", results)}";
    }

    [KernelFunction("run_unit_tests")]
    [Description("Runs unit tests for a specific project and returns results. Requires dotnet CLI.")]
    public async Task<string> RunUnitTestsAsync(
        Kernel kernel,
        [Description("Project name or path (e.g., tests/HiveOps.UnitTests).")] string projectPath,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        var repoRoot = FindRepoRoot();
        var fullProjectPath = Path.Combine(repoRoot, projectPath.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(fullProjectPath))
            return $"Project not found: {fullProjectPath}";

        // Validate project path to prevent command injection
        if (!IsValidProjectPath(fullProjectPath, repoRoot))
            return "Invalid project path. Path must be within the repository tests directory.";

        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            // Use ArgumentList for safe argument passing (no shell interpretation)
            psi.ArgumentList.Add("test");
            psi.ArgumentList.Add(fullProjectPath);
            psi.ArgumentList.Add("--no-build");
            psi.ArgumentList.Add("--verbosity");
            psi.ArgumentList.Add("quiet");

            using var process = Process.Start(psi);
            if (process is null)
                return "Failed to start dotnet test process.";

            await process.WaitForExitAsync(cancellationToken);
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);

            var success = process.ExitCode == 0;
            await LogDiagnosticAsync(tenantId, "run_unit_tests", $"ExitCode={process.ExitCode}\n{output}\n{error}", success, cancellationToken);

            return success
                ? $"Tests passed for {projectPath}."
                : $"Tests FAILED for {projectPath}. Output:\n{output}\n{error}";
        }
        catch (Exception ex)
        {
            await LogDiagnosticAsync(tenantId, "run_unit_tests", ex.Message, false, cancellationToken);
            return $"Test execution error: {ex.Message}";
        }
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private async Task LogDiagnosticAsync(Guid tenantId, string step, string result, bool success, CancellationToken ct)
    {
        // Find any open incident for this tenant and attach the log
        var incident = await _db.Incidents
            .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.Status != IncidentStatus.Closed, ct);

        if (incident is not null)
        {
            _db.DiagnosticLogs.Add(new DiagnosticLog
            {
                TenantId = tenantId,
                IncidentId = incident.Id,
                StepName = step,
                Result = result,
                IsSuccess = success
            });
            await _db.SaveChangesAsync(ct);
        }
    }

    private static string FindRepoRoot()
    {
        var current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")) ||
                Directory.Exists(Path.Combine(current, "src")) && File.Exists(Path.Combine(current, "HiveOps.slnx")))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            if (parent is null) break;
            current = parent.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    private static bool IsValidProjectPath(string projectPath, string repoRoot)
    {
        // Must be within repo
        var fullPath = Path.GetFullPath(Path.Combine(repoRoot, projectPath));
        var repoRootFull = Path.GetFullPath(repoRoot);

        if (!fullPath.StartsWith(repoRootFull, StringComparison.OrdinalIgnoreCase))
            return false;

        // Must be within tests directory
        var relativePath = fullPath[(repoRootFull.Length + 1)..];
        if (!relativePath.StartsWith("tests", StringComparison.OrdinalIgnoreCase))
            return false;

        // Must be a directory (project folder)
        if (!Directory.Exists(fullPath))
            return false;

        return true;
    }

    private static Guid GetTenantId(Kernel kernel)
    {
        if (kernel.Data.TryGetValue(KernelConstants.TenantIdKey, out var val) && val is Guid g && g != Guid.Empty)
            return g;

        throw new InvalidOperationException("TenantId is required for tenant-scoped plugin execution.");
    }
}
