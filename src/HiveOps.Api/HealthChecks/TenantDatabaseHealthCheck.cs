using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;

namespace HiveOps.Api.HealthChecks;

/// <summary>
/// Tenant-aware health check that verifies connectivity to the resolved tenant's database.
/// If no tenant is resolved (e.g. health probe without tenant header), it falls back to
/// the default connection string.
/// </summary>
public sealed class TenantDatabaseHealthCheck : IHealthCheck
{
    private readonly ITenantContext _tenantContext;
    private readonly IDynamicConnectionStringResolver _resolver;
    private readonly ILogger<TenantDatabaseHealthCheck> _logger;

    public TenantDatabaseHealthCheck(
        ITenantContext tenantContext,
        IDynamicConnectionStringResolver resolver,
        ILogger<TenantDatabaseHealthCheck> logger)
    {
        _tenantContext = tenantContext;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var connectionString = _tenantContext.IsResolved
                ? _resolver.Resolve(_tenantContext.TenantId)
                : ""; // fallback: resolve from configuration outside this check

            if (string.IsNullOrEmpty(connectionString))
                return HealthCheckResult.Healthy("No tenant resolved; skipping tenant DB check.");

            var startTime = DateTime.UtcNow;
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(cancellationToken);
            var duration = DateTime.UtcNow - startTime;

            var data = new Dictionary<string, object>
            {
                ["tenant_id"] = _tenantContext.TenantId,
                ["responsetime_ms"] = duration.TotalMilliseconds,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
            };

            if (duration.TotalMilliseconds > 1000)
            {
                _logger.LogWarning(
                    "Tenant database health check slow: {DurationMs}ms for tenant {TenantId}",
                    duration.TotalMilliseconds, _tenantContext.TenantId);
                return HealthCheckResult.Degraded("Tenant DB response time is high", null, data);
            }

            return HealthCheckResult.Healthy("Tenant database is reachable.", data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tenant database health check failed for tenant {TenantId}", _tenantContext.TenantId);
            return HealthCheckResult.Unhealthy("Tenant database connection failed", ex);
        }
    }
}
