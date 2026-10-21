using HiveOps.Domain.Entities;

namespace HiveOps.Domain.Interfaces;

/// <summary>
/// Append-only, SHA-256 hash-chained evidence ledger per incident. Every
/// meaningful step (triage, hypothesis, dry-run, approval, execution,
/// verification, rollback, confirmation) is recorded with a hash covering its
/// payload and the previous entry's hash, making tampering detectable.
/// </summary>
public interface IIncidentAuditLedger
{
    /// <summary>Appends a new chained entry; returns the stored entry with its computed hash.</summary>
    Task<IncidentAuditEntry> AppendAsync(
        Guid tenantId, Guid incidentId, string eventType, string actor,
        object payload, CancellationToken ct = default);

    /// <summary>Recomputes the whole chain and reports where it broke, if at all.</summary>
    Task<(bool IsValid, int TotalEntries, string? BrokenAtEntryId)> VerifyChainAsync(
        Guid tenantId, Guid incidentId, CancellationToken ct = default);
}
