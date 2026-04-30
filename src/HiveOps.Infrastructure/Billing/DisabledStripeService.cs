using HiveOps.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace HiveOps.Infrastructure.Billing;

/// <summary>
/// Billing disabled: no external calls. Used when <c>HiveOps:Deployment:Billing</c> is <c>None</c>.
/// </summary>
public sealed class DisabledStripeService : IStripeService
{
    private readonly ILogger<DisabledStripeService> _logger;

    public DisabledStripeService(ILogger<DisabledStripeService> logger)
    {
        _logger = logger;
    }

    public Task EnsureCustomerAsync(Guid tenantId, string tenantName, CancellationToken ct = default)
    {
        _logger.LogDebug("Billing disabled: EnsureCustomer skipped for tenant {TenantId}", tenantId);
        return Task.CompletedTask;
    }

    public Task EnsureSubscriptionAsync(Guid tenantId, string priceId, CancellationToken ct = default)
    {
        _logger.LogDebug("Billing disabled: EnsureSubscription skipped for tenant {TenantId}", tenantId);
        return Task.CompletedTask;
    }

    public Task<string?> CreatePortalSessionAsync(Guid tenantId, string returnUrl, CancellationToken ct = default)
    {
        _logger.LogDebug("Billing disabled: portal session not created for tenant {TenantId}", tenantId);
        return Task.FromResult<string?>(null);
    }

    public Task ReportUsageAsync(
        Guid tenantId,
        string metric,
        long quantity,
        DateTimeOffset? timestamp = null,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Billing disabled: ReportUsage skipped for tenant {TenantId}", tenantId);
        return Task.CompletedTask;
    }
}
