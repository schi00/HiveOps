using System.ComponentModel;
using System.Diagnostics;
using System.Text;
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
/// SelfSupportPlugin — allows the bot to inspect the tenant's own application
/// repository (per-tenant git workspace) and the HiveOps system database.
/// The repository is resolved via <see cref="IGitService.GetWorkspacePathAsync"/>.
/// </summary>
public sealed class SelfSupportPlugin
{
    private readonly AppDbContext _db;
    private readonly IConversationStateManager _stateManager;
    private readonly IGitService _gitService;

    public SelfSupportPlugin(AppDbContext db, IConversationStateManager stateManager, IGitService gitService)
    {
        _db = db;
        _stateManager = stateManager;
        _gitService = gitService;
    }

    // Valid file path pattern: alphanumeric, hyphens, underscores, dots, forward slashes
    // Must stay within the workspace, no parent directory traversal
    private static readonly Regex ValidSourcePathPattern = new(
        @"^[a-zA-Z0-9][a-zA-Z0-9._\-/]*\.(cs|cshtml|razor|ts|tsx|js|json|md|txt|yml|yaml|sql|xml|config)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] ValidExtensions =
        [".cs", ".cshtml", ".razor", ".ts", ".tsx", ".js", ".json", ".md", ".txt", ".yml", ".yaml", ".sql", ".xml", ".config"];

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages"
    };

    [KernelFunction("read_own_source")]
    [Description("Reads a source file from the tenant application repository to inspect code for diagnostics.")]
    public async Task<string> ReadOwnSourceAsync(
        Kernel kernel,
        [Description("Relative file path within the tenant repo (e.g., src/Web/Controllers/OrdersController.cs).")] string filePath,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);

        string repoRoot;
        try
        {
            repoRoot = await _gitService.GetWorkspacePathAsync(tenantId, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Unable to resolve the tenant repository workspace: {ex.Message}";
        }

        // Validate file path to prevent path traversal attacks
        if (!IsValidSourceFilePath(filePath))
            return "Invalid file path. Only source files within the repository are allowed.";

        var fullPath = Path.Combine(repoRoot, filePath.Replace('/', Path.DirectorySeparatorChar));
        fullPath = Path.GetFullPath(fullPath);

        // Ensure the resolved path is still within the workspace
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
    [Description("Searches the tenant application codebase for files matching a pattern or containing text. Returns numbered lines with context.")]
    public async Task<string> SearchOwnSourceAsync(
        Kernel kernel,
        [Description("Search term (alphanumeric only, max 50 chars).")] string pattern,
        [Description("File extension filter (e.g., .cs).")] string? extension = ".cs",
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);

        string repoRoot;
        try
        {
            repoRoot = await _gitService.GetWorkspacePathAsync(tenantId, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Unable to resolve the tenant repository workspace: {ex.Message}";
        }

        if (!Directory.Exists(repoRoot))
            return "Tenant repository workspace not found on this host.";

        // Sanitize pattern to prevent regex injection
        var sanitizedPattern = SanitizeSearchPattern(pattern);
        if (string.IsNullOrEmpty(sanitizedPattern))
            return "Invalid search pattern. Only alphanumeric characters, hyphens, and underscores are allowed.";

        // Validate extension
        if (!ValidExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return $"Invalid extension. Allowed: {string.Join(", ", ValidExtensions)}";

        const int MaxFiles = 10;
        const int MaxChars = 4000;
        const int ContextLines = 3;

        var result = new StringBuilder();
        var filesMatched = 0;

        foreach (var file in EnumerateWorkspaceFiles(repoRoot, $"*{extension}"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filesMatched >= MaxFiles || result.Length >= MaxChars)
                break;

            var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, cancellationToken);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            var matchIndices = new List<int>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(sanitizedPattern, StringComparison.OrdinalIgnoreCase))
                    matchIndices.Add(i);
            }

            var nameMatch = relative.Contains(sanitizedPattern, StringComparison.OrdinalIgnoreCase);
            if (matchIndices.Count == 0 && !nameMatch)
                continue;

            var block = new StringBuilder();
            block.Append(relative).Append('\n');

            foreach (var (start, end) in MergeContextRanges(matchIndices, ContextLines, lines.Length))
            {
                for (var i = start; i <= end; i++)
                    block.Append("  ").Append(i + 1).Append(" | ").Append(TruncateLine(lines[i])).Append('\n');
                block.Append("  ---\n");
            }

            if (result.Length + block.Length > MaxChars)
                break;

            result.Append(block);
            filesMatched++;
        }

        if (filesMatched == 0)
            return $"No matches for '{sanitizedPattern}' in *{extension} files.";

        return $"Found matches (showing up to {MaxFiles} files):\n{result}";
    }

    /// <summary>Merges ±<paramref name="context"/> line windows around each match into non-overlapping ranges.</summary>
    private static IEnumerable<(int Start, int End)> MergeContextRanges(List<int> matchIndices, int context, int lineCount)
    {
        var current = (Start: -1, End: -1);
        foreach (var index in matchIndices)
        {
            var start = Math.Max(0, index - context);
            var end = Math.Min(lineCount - 1, index + context);

            if (current.Start == -1)
            {
                current = (start, end);
            }
            else if (start <= current.End + 1)
            {
                current.End = Math.Max(current.End, end);
            }
            else
            {
                yield return current;
                current = (start, end);
            }
        }

        if (current.Start != -1)
            yield return current;
    }

    private static string TruncateLine(string line) =>
        line.Length > 200 ? line[..200] + "…" : line;

    /// <summary>
    /// Enumerates workspace files skipping build outputs and VCS internals
    /// (<c>.git</c>, <c>bin</c>, <c>obj</c>, <c>node_modules</c>, …).
    /// </summary>
    private static IEnumerable<string> EnumerateWorkspaceFiles(string root, string searchPattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, searchPattern).ToList();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;

            List<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var subdir in subdirs)
            {
                if (!ExcludedDirectories.Contains(Path.GetFileName(subdir)))
                    pending.Push(subdir);
            }
        }
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
    [Description("Runs basic health checks on the HiveOps system database (conversations, tenants, configuration). Does NOT inspect the tenant application database.")]
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

        return $"HiveOps system DB health:\n{string.Join("\n", results)}";
    }

    [KernelFunction("run_unit_tests")]
    [Description("Runs unit tests for a project inside the tenant repository workspace and returns results. Requires dotnet CLI.")]
    public async Task<string> RunUnitTestsAsync(
        Kernel kernel,
        [Description("Project name or path relative to the workspace (e.g., tests/HiveCrew.UnitTests). Defaults to the 'tests' directory.")] string projectPath,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);

        string repoRoot;
        try
        {
            repoRoot = await _gitService.GetWorkspacePathAsync(tenantId, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Unable to resolve the tenant repository workspace: {ex.Message}";
        }

        // Convention: test projects live under the "tests" directory of the workspace.
        var testsRoot = Path.Combine(Path.GetFullPath(repoRoot), "tests");
        if (!Directory.Exists(testsRoot))
            return "No 'tests' directory found under the tenant repository workspace.";

        var relativePath = string.IsNullOrWhiteSpace(projectPath) ? "tests" : projectPath;
        var fullProjectPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(fullProjectPath))
            return $"Project not found: {relativePath}";

        // Validate project path to prevent command injection / escaping the workspace
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
                ? $"Tests passed for {relativePath}."
                : $"Tests FAILED for {relativePath}. Output:\n{output}\n{error}";
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

    private static bool IsValidProjectPath(string projectPath, string repoRoot)
    {
        // Must be within workspace
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
