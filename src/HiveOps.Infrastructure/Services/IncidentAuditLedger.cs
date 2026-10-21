using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HiveOps.Infrastructure.Services;

/// <summary>
/// Append-only, SHA-256 hash-chained evidence ledger per incident.
/// Each entry's hash covers its content plus the previous entry's hash, so any
/// tampering with stored rows breaks the chain and is detected by
/// <see cref="VerifyChainAsync"/>.
///
/// The ledger always works in its own DI scope (same pattern as
/// <see cref="OutcomeMeteringService"/>) so it can be invoked safely from
/// background workers, controllers and agent flows regardless of the ambient
/// tenant context.
/// </summary>
public sealed class IncidentAuditLedger : IIncidentAuditLedger
{
    /// <summary>Sentinel previous-hash for the first entry of a chain.</summary>
    private const string GenesisHash = "GENESIS";

    /// <summary>Default actor for entries written by the automation pipeline.</summary>
    public const string AutoEngineActor = "HiveOps.AutoEngine";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IncidentAuditLedger> _logger;

    public IncidentAuditLedger(
        IServiceScopeFactory scopeFactory,
        ILogger<IncidentAuditLedger> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IncidentAuditEntry> AppendAsync(
        Guid tenantId, Guid incidentId, string eventType, string actor,
        object payload, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        ResolveTenantContext(scope, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var last = await db.IncidentAuditEntries
            .Where(e => e.TenantId == tenantId && e.IncidentId == incidentId)
            .OrderByDescending(e => e.SequenceNumber)
            .FirstOrDefaultAsync(ct);

        var sequenceNumber = (last?.SequenceNumber ?? 0) + 1;
        var previousHash = last?.EntryHash ?? GenesisHash;
        var payloadJson = JsonSerializer.Serialize(payload);
        var entryHash = ComputeEntryHash(
            tenantId, incidentId, sequenceNumber, eventType, actor, payloadJson, previousHash);

        var entry = new IncidentAuditEntry
        {
            TenantId = tenantId,
            IncidentId = incidentId,
            SequenceNumber = sequenceNumber,
            EventType = eventType,
            Actor = actor,
            PayloadJson = payloadJson,
            PreviousHash = previousHash,
            EntryHash = entryHash
        };

        db.IncidentAuditEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Audit entry {SequenceNumber} ({EventType}) appended for incident {IncidentId}: {EntryHash}",
            sequenceNumber, eventType, incidentId, entryHash);

        return entry;
    }

    /// <inheritdoc />
    public async Task<(bool IsValid, int TotalEntries, string? BrokenAtEntryId)> VerifyChainAsync(
        Guid tenantId, Guid incidentId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        ResolveTenantContext(scope, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entries = await db.IncidentAuditEntries
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.IncidentId == incidentId)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync(ct);

        var previousHash = GenesisHash;
        var expectedSequence = 1;
        foreach (var entry in entries)
        {
            var recomputed = ComputeEntryHash(
                entry.TenantId, entry.IncidentId, entry.SequenceNumber,
                entry.EventType, entry.Actor, entry.PayloadJson, entry.PreviousHash);

            var broken = entry.SequenceNumber != expectedSequence
                || !string.Equals(entry.PreviousHash, previousHash, StringComparison.Ordinal)
                || !string.Equals(entry.EntryHash, recomputed, StringComparison.Ordinal);

            if (broken)
            {
                _logger.LogWarning(
                    "Audit chain for incident {IncidentId} is broken at entry {EntryId} (sequence {SequenceNumber}).",
                    incidentId, entry.Id, entry.SequenceNumber);
                return (false, entries.Count, entry.Id.ToString());
            }

            previousHash = entry.EntryHash;
            expectedSequence++;
        }

        return (true, entries.Count, null);
    }

    /// <summary>
    /// SHA-256 over the canonical entry payload:
    /// <c>{TenantId}|{IncidentId}|{SequenceNumber}|{EventType}|{Actor}|{PayloadJson}|{PreviousHash}</c>
    /// rendered as lowercase hex.
    /// </summary>
    private static string ComputeEntryHash(
        Guid tenantId, Guid incidentId, int sequenceNumber,
        string eventType, string actor, string payloadJson, string previousHash)
    {
        var canonical = string.Join('|',
            tenantId, incidentId, sequenceNumber, eventType, actor, payloadJson, previousHash);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Resolves the scope tenant so EF query filters stay tenant-correct
    /// (same pattern as IncidentAutoWorkflowService / OutcomeMeteringService).
    /// </summary>
    private static void ResolveTenantContext(IServiceScope scope, Guid tenantId)
    {
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        if (tenantContext is TenantContext tc && !tc.IsResolved)
            tc.SetTenant(tenantId);
    }
}
