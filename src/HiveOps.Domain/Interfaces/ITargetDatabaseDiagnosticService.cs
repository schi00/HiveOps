using HiveOps.Domain.Models;

namespace HiveOps.Domain.Interfaces;

/// <summary>
/// Read-only diagnostics over a tenant's own application database (e.g. HiveCrew).
/// The connection string is resolved per-tenant via <see cref="IDynamicConnectionStringResolver"/>;
/// all queries run under READ UNCOMMITTED with short timeouts and never mutate data.
/// </summary>
public interface ITargetDatabaseDiagnosticService
{
    /// <summary>
    /// Builds a compact DDL summary of the tenant tables whose names match keywords
    /// extracted from <paramref name="incidentText"/>; when nothing matches, returns the
    /// most central tables (most referenced by foreign keys). Results are cached briefly.
    /// </summary>
    Task<TargetSchemaSummary> GetRelevantSchemaAsync(Guid tenantId, string incidentText, CancellationToken ct = default);

    /// <summary>
    /// Executes a SELECT-only query against the tenant database and renders the result
    /// as a markdown table (row and cell limits apply). Non-SELECT statements are rejected.
    /// </summary>
    Task<TargetQueryResult> ExecuteReadOnlyQueryAsync(Guid tenantId, string sqlQuery, CancellationToken ct = default);
}
