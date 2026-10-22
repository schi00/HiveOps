using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using HiveOps.Application.Interfaces;
using HiveOps.Domain.Entities;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Infrastructure.AI;

/// <summary>
/// Durable, tenant-scoped pool of unfinished AI tasks. Enqueue is idempotent
/// per (TenantId, DedupeKey): an existing Pending task is kept and pulled
/// forward rather than duplicated. Enqueue never throws — a queue write
/// failure must not cascade into the caller's operation.
/// </summary>
public sealed class LlmTaskQueueService : ILlmTaskQueue
{
    private const int MaxListResults = 200;
    private const int MaxErrorChars = 1900;

    private readonly AppDbContext _db;
    private readonly ILogger<LlmTaskQueueService> _logger;

    public LlmTaskQueueService(AppDbContext db, ILogger<LlmTaskQueueService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LlmPendingTask?> EnqueueAsync(
        Guid tenantId, string taskType, string payloadJson, string dedupeKey,
        string? lastError = null, CancellationToken ct = default)
    {
        try
        {
            var existing = await _db.LlmPendingTasks
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.TenantId == tenantId
                    && t.DedupeKey == dedupeKey
                    && t.Status == LlmPendingTask.StatusPending, ct);
            if (existing is not null)
            {
                // Pull forward: a fresh failure means the work is still needed.
                existing.NextAttemptAtUtc = DateTimeOffset.UtcNow;
                existing.LastError = Truncate(lastError, MaxErrorChars);
                existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                return existing;
            }

            var task = new LlmPendingTask
            {
                TenantId = tenantId,
                TaskType = taskType,
                PayloadJson = payloadJson,
                DedupeKey = dedupeKey,
                LastError = Truncate(lastError, MaxErrorChars)
            };
            _db.LlmPendingTasks.Add(task);
            await _db.SaveChangesAsync(ct);
            return task;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Failed to enqueue LLM task {TaskType} ({DedupeKey}) for tenant {TenantId}; work item may need manual retry.",
                taskType, dedupeKey, tenantId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LlmPendingTask>> ListAsync(
        Guid tenantId, string? status, int take = 50, CancellationToken ct = default)
    {
        var query = _db.LlmPendingTasks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(t => t.Status == status);

        return await query
            .OrderByDescending(t => t.UpdatedAtUtc)
            .Take(Math.Clamp(take, 1, MaxListResults))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(Guid tenantId, Guid taskId, CancellationToken ct = default)
    {
        var task = await _db.LlmPendingTasks
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Id == taskId, ct);
        if (task is null || task.Status != LlmPendingTask.StatusPending)
            return false;

        task.Status = LlmPendingTask.StatusCancelled;
        task.UpdatedAtUtc = DateTimeOffset.UtcNow;
        task.CompletedAtUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RetryNowAsync(Guid tenantId, Guid taskId, CancellationToken ct = default)
    {
        var task = await _db.LlmPendingTasks
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Id == taskId, ct);
        if (task is null)
            return false;

        // Reopen finished tasks too (except Cancelled) — a human asked for another go.
        if (task.Status == LlmPendingTask.StatusCancelled)
            return false;

        task.Status = LlmPendingTask.StatusPending;
        task.NextAttemptAtUtc = DateTimeOffset.UtcNow;
        task.CompletedAtUtc = null;
        task.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (task.Attempts >= task.MaxAttempts)
            task.Attempts = 0; // give the manual retry a fresh budget
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
