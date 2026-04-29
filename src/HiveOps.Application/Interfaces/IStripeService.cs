namespace HiveOps.Application.Interfaces;

public interface IStripeService
{
    Task EnsureCustomerAsync(Guid tenantId, string tenantName, CancellationToken ct = default);
    Task EnsureSubscriptionAsync(Guid tenantId, string priceId, CancellationToken ct = default);
    Task<string?> CreatePortalSessionAsync(Guid tenantId, string returnUrl, CancellationToken ct = default);
    Task ReportUsageAsync(Guid tenantId, string metric, long quantity, DateTimeOffset? timestamp = null, string? idempotencyKey = null, CancellationToken ct = default);
}
