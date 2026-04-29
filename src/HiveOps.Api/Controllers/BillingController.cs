using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Billing;
using HiveOps.Application.Interfaces;
using System.Security.Cryptography;

namespace HiveOps.Api.Controllers;

[ApiController]
[Route("api/billing/stripe")]
public sealed class BillingController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<BillingController> _logger;
    private readonly IOptions<StripeOptions> _stripeOptions;
    private readonly IStripeService _stripeService;

    public BillingController(AppDbContext db, ILogger<BillingController> logger, IOptions<StripeOptions> stripeOptions, IStripeService stripeService)
    {
        _db = db;
        _logger = logger;
        _stripeOptions = stripeOptions;
        _stripeService = stripeService;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook()
    {
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();
        var secret = _stripeOptions.Value.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret)) return BadRequest("Webhook secret not configured");
#if STRIPE_SDK
        Stripe.Event stripeEvent;
        try
        {
            stripeEvent = Stripe.EventUtility.ConstructEvent(json, Request.Headers["Stripe-Signature"], secret);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stripe signature validation failed");
            return Unauthorized();
        }

        try
        {
            switch (stripeEvent.Type)
            {
                case "customer.subscription.created":
                case "customer.subscription.updated":
                case "customer.subscription.deleted":
                {
                    var sub = stripeEvent.Data.Object as Stripe.Subscription;
                    if (sub is null) return Ok();
                    var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.StripeSubscriptionId == sub.Id || t.StripeCustomerId == sub.CustomerId);
                    if (tenant is null) return Ok();
                    tenant.SubscriptionStatus = sub.Status;
                    tenant.SubscriptionCurrentPeriodEnd = sub.CurrentPeriodEnd.HasValue ? new DateTimeOffset(sub.CurrentPeriodEnd.Value) : tenant.SubscriptionCurrentPeriodEnd;
                    if (!string.IsNullOrWhiteSpace(sub.Id)) tenant.StripeSubscriptionId = sub.Id;
                    if (!string.IsNullOrWhiteSpace(sub.CustomerId)) tenant.StripeCustomerId = sub.CustomerId;
                    if (string.IsNullOrWhiteSpace(tenant.StripeSubscriptionItemId) && sub.Items?.Data?.Count > 0)
                        tenant.StripeSubscriptionItemId = sub.Items.Data[0].Id;
                    await _db.SaveChangesAsync();
                    return Ok();
                }
                default:
                    _logger.LogInformation("Stripe event received: {Type}", stripeEvent.Type);
                    return Ok();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process Stripe webhook");
            return BadRequest();
        }
#else
        _logger.LogInformation("Stripe SDK not active; ignoring webhook");
        return Ok();
#endif
    }

    [HttpGet("portal-link")]
    public async Task<IActionResult> GetPortalLink([FromQuery] Guid tenantId, [FromQuery] string? returnUrl)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant is null) return NotFound();
        if (string.IsNullOrWhiteSpace(tenant.StripeCustomerId)) return BadRequest("CustomerId not found for tenant");

        // Prefer SDK-based portal session when available
        var safeReturn = string.IsNullOrWhiteSpace(returnUrl)
            ? Url.Content("~/dashboard#admin") ?? "/dashboard#admin"
            : returnUrl!;

        var url = await _stripeService.CreatePortalSessionAsync(tenant.Id, safeReturn, HttpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(url))
        {
            // Fallback to configured base url if provided
            var baseUrl = _stripeOptions.Value.PortalBaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl)) return BadRequest("Portal is not configured");
            var fb = baseUrl.Contains("?")
                ? $"{baseUrl}&customer={Uri.EscapeDataString(tenant.StripeCustomerId)}"
                : $"{baseUrl}?customer={Uri.EscapeDataString(tenant.StripeCustomerId)}";
            return Ok(new { url = fb });
        }
        return Ok(new { url });
    }
}
