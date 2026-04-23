using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Api.HealthChecks;

/// <summary>
/// Custom health check that verifies critical system components.
/// Used for monitoring and orchestration (e.g., Kubernetes liveness probes).
/// </summary>
public sealed class SystemHealthCheck : IHealthCheck
{
    private readonly AppDbContext _db;
    private readonly ILogger<SystemHealthCheck> _logger;

    public SystemHealthCheck(AppDbContext db, ILogger<SystemHealthCheck> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var startTime = DateTime.UtcNow;
            
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1", cancellationToken);
            
            var duration = DateTime.UtcNow - startTime;
            
            var data = new Dictionary<string, object>
            {
                ["database"] = "healthy",
                ["responsetime_ms"] = duration.TotalMilliseconds,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
            };

            if (duration.TotalMilliseconds > 1000)
            {
                _logger.LogWarning("Database health check slow: {DurationMs}ms", duration.TotalMilliseconds);
                return new HealthCheckResult(HealthStatus.Degraded, "Database response time is high", null, data);
            }

            return new HealthCheckResult(HealthStatus.Healthy, "System is healthy", null, data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");
            return new HealthCheckResult(HealthStatus.Unhealthy, "Database connection failed", ex);
        }
    }
}
