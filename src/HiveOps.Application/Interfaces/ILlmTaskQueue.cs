using HiveOps.Domain.Entities;

namespace HiveOps.Application.Interfaces;

/// <summary>
/// Durable pool of AI tasks that could not finish (e.g. total LLM outage).
/// Producers enqueue idempotently by DedupeKey; the retry worker claims due
/// tasks, replays them, and marks completion or reschedules with backoff.
/// </summary>
public interface ILlmTaskQueue
{
    /// <summary>
    /// Enqueues a task idempotently: if a Pending task already exists for the
    /// same DedupeKey it is kept (and pulled forward for a quicker retry).
    /// Returns the existing or newly created task. Never throws on unexpected
    /// errors — enqueue failure must not break the calling operation.
    /// </summary>
    Task<LlmPendingTask?> EnqueueAsync(
        Guid tenantId, string taskType, string payloadJson, string dedupeKey,
        string? lastError = null, CancellationToken ct = default);

    /// <summary>Lists tasks for a tenant; status null returns all.</summary>
    Task<IReadOnlyList<LlmPendingTask>> ListAsync(
        Guid tenantId, string? status, int take = 50, CancellationToken ct = default);

    /// <summary>Marks a Pending task cancelled (manual review). Returns false if not found/not Pending.</summary>
    Task<bool> CancelAsync(Guid tenantId, Guid taskId, CancellationToken ct = default);

    /// <summary>Reschedules a task for immediate retry (NextAttemptAtUtc = now). Returns false if not found/finished.</summary>
    Task<bool> RetryNowAsync(Guid tenantId, Guid taskId, CancellationToken ct = default);
}
