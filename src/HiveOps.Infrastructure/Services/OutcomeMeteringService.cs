using System.Text.Json;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HiveOps.Infrastructure.Services;

/// <summary>
/// Outcome-based billing metering. Records billable "verified autonomous
/// resolutions" as <see cref="DiagnosticLog"/> entries (idempotent, at most one
/// per incident) and aggregates per-tenant outcome metrics for a reporting
/// window, enabling outcome-based pricing.
/// </summary>
public sealed class OutcomeMeteringService : IOutcomeMeteringService
{
    /// <summary>DiagnosticLog step name that marks a billable verified resolution.</summary>
    public const string VerifiedResolutionStepName = "billing.verified_resolution";

    /// <summary>DiagnosticLog step name written when a deploy rollback is requested.</summary>
    public const string RollbackRequestedStepName = "rollback.requested";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutcomeMeteringService> _logger;

    public OutcomeMeteringService(
        IServiceScopeFactory scopeFactory,
        ILogger<OutcomeMeteringService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RecordVerifiedResolutionAsync(Incident incident, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        ResolveTenantContext(scope, incident.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Idempotent: a single billable resolution per incident, even when the
        // confirmation signal (button callback / verification query) fires twice.
        var alreadyRecorded = await db.DiagnosticLogs.AnyAsync(
            l => l.IncidentId == incident.Id && l.StepName == VerifiedResolutionStepName,
            ct);
        if (alreadyRecorded)
        {
            _logger.LogDebug(
                "Verified resolution already recorded for incident {IncidentId}; skipping duplicate billing event.",
                incident.Id);
            return;
        }

        db.DiagnosticLogs.Add(new DiagnosticLog
        {
            TenantId = incident.TenantId,
            IncidentId = incident.Id,
            StepName = VerifiedResolutionStepName,
            IsSuccess = true,
            Result = JsonSerializer.Serialize(new
            {
                resolutionMode = incident.ResolutionMode.ToString(),
                category = incident.Category.ToString(),
                triageConfidence = incident.TriageConfidence,
                verificationPassed = incident.VerificationPassed,
                sourceChannel = incident.SourceChannel,
                at = DateTimeOffset.UtcNow
            })
        });
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Recorded verified autonomous resolution for incident {IncidentId} (tenant {TenantId}, mode {ResolutionMode}).",
            incident.Id, incident.TenantId, incident.ResolutionMode);
    }

    /// <inheritdoc />
    public async Task<TenantOutcomeSummary> GetSummaryAsync(
        Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        ResolveTenantContext(scope, tenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // All queries stay tenant-scoped via the EF query filters bound to the
        // tenant context resolved above — no IgnoreQueryFilters needed.
        var totalIncidents = await db.Incidents.CountAsync(
            i => i.CreatedAt >= from && i.CreatedAt <= to,
            ct);

        var verifiedAutonomousResolutions = await db.DiagnosticLogs
            .Where(l => l.StepName == VerifiedResolutionStepName
                        && l.CreatedAt >= from && l.CreatedAt <= to)
            .Select(l => l.IncidentId)
            .Distinct()
            .CountAsync(ct);

        var assistedResolutions = await db.Incidents.CountAsync(
            i => i.ResolvedAt != null
                 && i.ResolvedAt >= from && i.ResolvedAt <= to
                 && (i.ResolutionMode == IncidentResolutionMode.Assisted || i.AssignedTo == "engineer"),
            ct);

        // Rollbacks are counted once per incident: union of explicit
        // "rollback.requested" diagnostic entries and reverted remediation
        // snapshots within the window.
        var rollbackRequestedIncidentIds = await db.DiagnosticLogs
            .Where(l => l.StepName == RollbackRequestedStepName
                        && l.CreatedAt >= from && l.CreatedAt <= to)
            .Select(l => l.IncidentId)
            .Distinct()
            .ToListAsync(ct);

        var revertedSnapshotIncidentIds = await db.IncidentRemediationSnapshots
            .Where(s => s.IsReverted
                        && s.RevertedAt != null
                        && s.RevertedAt >= from && s.RevertedAt <= to)
            .Select(s => s.IncidentId)
            .Distinct()
            .ToListAsync(ct);

        var rollbacksCount = rollbackRequestedIncidentIds
            .Union(revertedSnapshotIncidentIds)
            .Count();

        return new TenantOutcomeSummary(
            tenantId,
            from,
            to,
            totalIncidents,
            verifiedAutonomousResolutions,
            assistedResolutions,
            rollbacksCount);
    }

    /// <summary>
    /// Resolves the scope tenant so EF query filters stay tenant-correct
    /// (same pattern as IncidentAutoWorkflowService).
    /// </summary>
    private static void ResolveTenantContext(IServiceScope scope, Guid tenantId)
    {
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        if (tenantContext is TenantContext tc && !tc.IsResolved)
            tc.SetTenant(tenantId);
    }
}
