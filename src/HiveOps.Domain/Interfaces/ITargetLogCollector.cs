using HiveOps.Domain.Models;

namespace HiveOps.Domain.Interfaces;

/// <summary>
/// Collects recent application error/warning logs from the tenant's target
/// system (log tables, diagnostics endpoints, etc.) for incident correlation.
/// </summary>
public interface ITargetLogCollector
{
    /// <summary>
    /// Returns recent error-level log entries correlated with the incident.
    /// Implementations must degrade gracefully: no log source => empty list.
    /// </summary>
    Task<IReadOnlyList<AppLogEvidence>> CollectRecentErrorsAsync(
        Guid tenantId, string searchHint, TimeSpan lookback, CancellationToken ct = default);
}
