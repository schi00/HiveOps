using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Api.Services;

/// <summary>
/// Background hosted service that warms the tenant connection-string cache
/// at application startup by enumerating active tenants and pre-loading their
/// resolved connection strings into IMemoryCache.
/// </summary>
public sealed class TenantCacheWarmerHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TenantCacheWarmerHostedService> _logger;

    public TenantCacheWarmerHostedService(
        IServiceProvider serviceProvider,
        ILogger<TenantCacheWarmerHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting tenant cache warmer...");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<IDynamicConnectionStringResolver>();

        var activeTenantIds = await db.Tenants
            .AsNoTracking()
            .Where(t => t.IsActive)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        foreach (var tenantId in activeTenantIds)
        {
            try
            {
                await resolver.WarmCacheAsync(tenantId, cancellationToken);
                _logger.LogDebug("Warmed connection-string cache for tenant {TenantId}", tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to warm cache for tenant {TenantId}", tenantId);
            }
        }

        _logger.LogInformation(
            "Tenant cache warmer completed. {SuccessCount}/{TotalCount} tenants warmed.",
            activeTenantIds.Count,
            activeTenantIds.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
