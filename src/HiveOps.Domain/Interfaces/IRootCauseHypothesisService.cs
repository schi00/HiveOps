using HiveOps.Domain.Entities;
using HiveOps.Domain.Models;

namespace HiveOps.Domain.Interfaces;

/// <summary>
/// Multi-source investigation engine: gathers DB diagnostics, application logs
/// and recent git history, then produces a structured root-cause bundle with a
/// recommended remediation track and a post-fix verification query.
/// </summary>
public interface IRootCauseHypothesisService
{
    Task<RootCauseBundle> InvestigateAsync(Incident incident, CancellationToken ct = default);

    /// <summary>
    /// Runs the stored verification query against the tenant DB. Returns true
    /// when the query reports zero anomaly rows (problem resolved).
    /// </summary>
    Task<bool> VerifyPostFixConditionAsync(Guid tenantId, string verificationSqlQuery, CancellationToken ct = default);
}
