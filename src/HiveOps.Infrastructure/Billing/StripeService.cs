using HiveOps.Application.Interfaces;
using HiveOps.Infrastructure.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Infrastructure.Billing;

public sealed class StripeService : IStripeService
{
    private readonly ILogger<StripeService> _logger;
    private readonly ISecretProvider _secrets;
    private readonly StripeOptions _options;
    private readonly AppDbContext _db;

    public StripeService(ILogger<StripeService> logger, ISecretProvider secrets, IOptions<StripeOptions> options, AppDbContext db)
    {
        _logger = logger;
        _secrets = secrets;
        _options = options.Value ?? new StripeOptions();
        _db = db;
    }

    public async Task EnsureCustomerAsync(Guid tenantId, string tenantName, CancellationToken ct = default)
    {
        _logger.LogInformation("[Stripe] EnsureCustomer tenant={TenantId} name={Name}", tenantId, tenantName);
#if STRIPE_SDK
        var apiKey = _options.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("[Stripe] ApiKey missing; cannot ensure customer");
            return;
        }

        Stripe.StripeConfiguration.ApiKey = apiKey;
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
        {
            _logger.LogWarning("[Stripe] Tenant not found {TenantId}", tenantId);
            return;
        }

        if (!string.IsNullOrWhiteSpace(tenant.StripeCustomerId))
            return;

        var custService = new Stripe.CustomerService();
        var cust = await custService.CreateAsync(new Stripe.CustomerCreateOptions
        {
            Name = tenantName,
            Metadata = new Dictionary<string, string> { ["tenantId", tenant.Id.ToString()] }
        }, cancellationToken: ct);
        tenant.StripeCustomerId = cust.Id;
        await _db.SaveChangesAsync(ct);
#else
        _logger.LogDebug("[Stripe] SDK not compiled in; EnsureCustomer is a no-op");
        await Task.CompletedTask;
#endif
    }

    public async Task EnsureSubscriptionAsync(Guid tenantId, string priceId, CancellationToken ct = default)
    {
        _logger.LogInformation("[Stripe] EnsureSubscription tenant={TenantId} price={Price}", tenantId, priceId);
#if STRIPE_SDK
        var apiKey = _options.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("[Stripe] ApiKey missing; cannot ensure subscription");
            return;
        }
        Stripe.StripeConfiguration.ApiKey = apiKey;

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) { _logger.LogWarning("[Stripe] Tenant not found {TenantId}", tenantId); return; }

        // Ensure customer
        if (string.IsNullOrWhiteSpace(tenant.StripeCustomerId))
        {
            var custService = new Stripe.CustomerService();
            var cust = await custService.CreateAsync(new Stripe.CustomerCreateOptions
            {
                Name = tenant.Name,
                Metadata = new Dictionary<string, string>{{"tenantId", tenant.Id.ToString()}}
            }, cancellationToken: ct);
            tenant.StripeCustomerId = cust.Id;
        }

        // Ensure subscription
        if (string.IsNullOrWhiteSpace(tenant.StripeSubscriptionId))
        {
            var subService = new Stripe.SubscriptionService();
            var sub = await subService.CreateAsync(new Stripe.SubscriptionCreateOptions
            {
                Customer = tenant.StripeCustomerId,
                Items = new List<Stripe.SubscriptionItemOptions>
                {
                    new Stripe.SubscriptionItemOptions { Price = priceId }
                }
            }, cancellationToken: ct);
            tenant.StripeSubscriptionId = sub.Id;
            tenant.StripePriceId = priceId;
            // Persist the first item's id for metered usage
            var item = sub.Items?.Data != null && sub.Items.Data.Count > 0 ? sub.Items.Data[0] : null;
            if (item != null) tenant.StripeSubscriptionItemId = item.Id;
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            // Optionally refresh subscription item if missing
            if (string.IsNullOrWhiteSpace(tenant.StripeSubscriptionItemId))
            {
                var subService = new Stripe.SubscriptionService();
                var sub = await subService.GetAsync(tenant.StripeSubscriptionId, cancellationToken: ct);
                var item = sub.Items?.Data != null && sub.Items.Data.Count > 0 ? sub.Items.Data[0] : null;
                if (item != null)
                {
                    tenant.StripeSubscriptionItemId = item.Id;
                    await _db.SaveChangesAsync(ct);
                }
            }
        }
#else
        await Task.CompletedTask;
#endif
    }

    public async Task<string?> CreatePortalSessionAsync(Guid tenantId, string returnUrl, CancellationToken ct = default)
    {
        try
        {
            var apiKey = _options.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("[Stripe] ApiKey missing; portal session not created");
                return null;
            }
#if STRIPE_SDK
            Stripe.StripeConfiguration.ApiKey = apiKey;
            var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
            if (tenant is null || string.IsNullOrWhiteSpace(tenant.StripeCustomerId))
            {
                _logger.LogWarning("[Stripe] Tenant or StripeCustomerId missing for tenant {TenantId}", tenantId);
                return null;
            }

            var sessionService = new Stripe.BillingPortal.SessionService();
            var session = await sessionService.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
            {
                Customer = tenant.StripeCustomerId,
                ReturnUrl = returnUrl
            }, cancellationToken: ct);
            return session?.Url;
#else
            _logger.LogInformation("[Stripe] SDK not compiled in; returning null portal session");
            await Task.Yield();
            return null;
#endif
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Stripe] Failed to create portal session for tenant {TenantId}", tenantId);
            return null;
        }
    }

    public async Task ReportUsageAsync(Guid tenantId, string metric, long quantity, DateTimeOffset? timestamp = null, string? idempotencyKey = null, CancellationToken ct = default)
    {
        _logger.LogInformation("[Stripe] ReportUsage tenant={TenantId} metric={Metric} qty={Qty} at={At} idem={Idem}", tenantId, metric, quantity, timestamp ?? DateTimeOffset.UtcNow, idempotencyKey);
#if STRIPE_SDK
        try
        {
            var apiKey = _options.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("[Stripe] ApiKey missing; usage record not sent");
                return;
            }
            Stripe.StripeConfiguration.ApiKey = apiKey;
            var usageService = new Stripe.UsageRecordService();
            // Resolve subscription item id from tenant
            var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
            if (tenant is null || string.IsNullOrWhiteSpace(tenant.StripeSubscriptionItemId))
            {
                _logger.LogWarning("[Stripe] SubscriptionItemId missing for tenant {TenantId}; cannot report usage", tenantId);
                return;
            }
            var create = new Stripe.UsageRecordCreateOptions
            {
                Quantity = quantity,
                Action = "increment",
                Timestamp = (timestamp ?? DateTimeOffset.UtcNow).UtcDateTime
            };
            var req = new Stripe.RequestOptions();
            if (!string.IsNullOrWhiteSpace(idempotencyKey)) req.IdempotencyKey = idempotencyKey;
            await usageService.CreateAsync(tenant.StripeSubscriptionItemId, create, req, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Stripe] Failed to send usage record for tenant {TenantId}", tenantId);
        }
#else
        await Task.CompletedTask;
#endif
    }
}
