using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
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

    [KernelFunction("read_own_source")]
    [Description("Reads a source file from the HiveOps repository to inspect code for diagnostics.")]
    public async Task<string> ReadOwnSourceAsync(
        Kernel kernel,
        [Description("Relative file path within the repo (e.g., src/HiveOps.Agents/Support/SupportPlugin.cs).")] string filePath,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        var repoRoot = FindRepoRoot();
        var fullPath = Path.Combine(repoRoot, filePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(fullPath))
            return $"File not found: {fullPath}";

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);

        // Store as diagnostic log for the active incident if any
        await LogDiagnosticAsync(tenantId, "read_own_source", $"Read {filePath} ({content.Length} chars)", true, cancellationToken);

        // Return a truncated preview
        var preview = content.Length > 2000 ? content[..2000] + "\n... (truncated)" : content;
        return $"```csharp\n{preview}\n```";
    }

    [KernelFunction("search_own_source")]
    [Description("Searches the HiveOps codebase for files matching a pattern or containing text.")]
    public Task<string> SearchOwnSourceAsync(
        Kernel kernel,
        [Description("Glob pattern or search term.")] string pattern,
        [Description("File extension filter (e.g., .cs).")] string? extension = ".cs",
        CancellationToken cancellationToken = default)
    {
        var repoRoot = FindRepoRoot();
        var srcPath = Path.Combine(repoRoot, "src");
        if (!Directory.Exists(srcPath))
            return Task.FromResult("Source directory not found.");

        var files = Directory.EnumerateFiles(srcPath, $"*{extension}", SearchOption.AllDirectories)
            .Where(f => f.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                        File.ReadAllText(f).Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .Select(f => f.Replace(repoRoot + Path.DirectorySeparatorChar, "").Replace('\\', '/'));

        return Task.FromResult($"Found matches:\n{string.Join("\n", files)}");
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

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"test \"{fullProjectPath}\" --no-build --verbosity quiet",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

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

    private static Guid GetTenantId(Kernel kernel)
    {
        if (kernel.Data.TryGetValue(KernelConstants.TenantIdKey, out var val) && val is Guid g && g != Guid.Empty)
            return g;

        throw new InvalidOperationException("TenantId is required for tenant-scoped plugin execution.");
    }
}
