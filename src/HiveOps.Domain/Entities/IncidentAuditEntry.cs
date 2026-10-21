using HiveOps.Domain.Interfaces;

namespace HiveOps.Domain.Entities;

/// <summary>
/// Append-only, hash-chained audit entry for an incident. Each entry's hash
/// covers its content plus the previous entry's hash, forming a tamper-evident
/// evidence chain verifiable by the dashboard/audit endpoint.
/// </summary>
public sealed class IncidentAuditEntry : ITenantScoped
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid IncidentId { get; set; }

    /// <summary>Monotonic sequence within the incident chain (1-based).</summary>
    public int SequenceNumber { get; set; }

    /// <summary>"Triage" | "Hypothesis" | "Playbook" | "DryRun" | "Approval" | "Execution" | "Verification" | "Rollback" | "PullRequest" | "ConfirmedClosed".</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Who produced the entry: "HiveOps.AutoEngine", agent name, or admin identity.</summary>
    public string Actor { get; set; } = "HiveOps.AutoEngine";

    /// <summary>Structured JSON payload describing the event (queries run, rows affected, hashes, decisions).</summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>EntryHash of the previous entry in the chain, or "GENESIS" for the first.</summary>
    public string PreviousHash { get; set; } = string.Empty;

    /// <summary>SHA-256 hex of "{TenantId}|{IncidentId}|{SequenceNumber}|{EventType}|{Actor}|{PayloadJson}|{PreviousHash}".</summary>
    public string EntryHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // Navigation
    public Incident Incident { get; set; } = null!;
}
