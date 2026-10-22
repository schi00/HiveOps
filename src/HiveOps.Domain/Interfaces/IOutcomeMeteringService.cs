using HiveOps.Domain.Entities;

namespace HiveOps.Domain.Interfaces;

/// <summary>Tenant outcome metrics for a billing/reporting window.</summary>
public sealed record TenantOutcomeSummary(
    Guid TenantId,
    DateTimeOffset From,
    DateTimeOffset To,
    int TotalIncidents,
    int VerifiedAutonomousResolutions,
    int AssistedResolutions,
    int RollbacksCount,
    int ShadowProposedResolutions = 0,
    double EstimatedHoursSaved = 0,
    decimal EstimatedCostSavingsUsd = 0m);

/// <summary>
/// Records billable "verified autonomous resolutions" (user-confirmed or
/// verification-query-passed) and aggregates outcome metrics per tenant,
/// enabling outcome-based pricing.
/// </summary>
public interface IOutcomeMeteringService
{
    /// <summary>Idempotent: at most one billable resolution recorded per incident.</summary>
    Task RecordVerifiedResolutionAsync(Incident incident, CancellationToken ct = default);

    /// <summary>Idempotent: records a proposed resolution generated in Shadow / POC mode without executing mutations.</summary>
    Task RecordShadowProposedResolutionAsync(Incident incident, string proposedTrack, string summary, CancellationToken ct = default);

    Task<TenantOutcomeSummary> GetSummaryAsync(
        Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
