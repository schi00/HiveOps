using HiveOps.Domain.Interfaces;

namespace HiveOps.Domain.Entities;

/// <summary>
/// Durable pool entry for AI work that could not complete because every LLM
/// provider was unavailable (total outage) or the attempt failed. A background
/// worker replays tasks with exponential backoff until they succeed or reach
/// MaxAttempts, guaranteeing no correction/enrichment is silently dropped.
/// </summary>
public sealed class LlmPendingTask : ITenantScoped
{
    public const string TaskDocDistill = "DocDistill";
    public const string TaskDbProfile = "DbProfile";
    public const string TaskImprovementDraft = "ImprovementDraft";

    public const string StatusPending = "Pending";
    public const string StatusCompleted = "Completed";
    public const string StatusDeadLetter = "DeadLetter";
    public const string StatusCancelled = "Cancelled";

    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>One of Task* constants; drives worker dispatch.</summary>
    public string TaskType { get; set; } = string.Empty;

    /// <summary>Inputs needed to replay the work (e.g. { "documentId": "..." }).</summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>Idempotency key: one Pending task per key per tenant.</summary>
    public string DedupeKey { get; set; } = string.Empty;

    public string Status { get; set; } = StatusPending;

    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 12;

    public DateTimeOffset NextAttemptAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
