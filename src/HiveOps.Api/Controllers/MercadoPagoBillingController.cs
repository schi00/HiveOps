using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HiveOps.Api.Authentication;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Models;
using HiveOps.Domain.Entities;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Api.Controllers;

/// <summary>
/// Mercado Pago billing for HiveOps subscriptions. Checkout creates a per-payment
/// preference whose notification_url points back here — the MP account is shared
/// with other HiveCorp products, so routing is per-resource, not app-level.
/// The webhook re-fetches the payment from MP before applying any change.
/// </summary>
[ApiController]
[Route("api/billing/mercadopago")]
public sealed class MercadoPagoBillingController : ControllerBase
{
    // Matches TenantConfigService.JsonOptions so the ConfigJson envelope round-trips identically.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AppDbContext _db;
    private readonly TenantContext _tenantContext;
    private readonly IMercadoPagoService _mp;
    private readonly ILogger<MercadoPagoBillingController> _logger;

    public MercadoPagoBillingController(
        AppDbContext db,
        TenantContext tenantContext,
        IMercadoPagoService mp,
        ILogger<MercadoPagoBillingController> logger)
    {
        _db = db;
        _tenantContext = tenantContext;
        _mp = mp;
        _logger = logger;
    }

    private bool IsPrivileged => User.IsInRole(AppRoles.SuperAdmin) || User.IsInRole(AppRoles.Admin);

    /// <summary>Creates a Checkout Pro preference and returns the payer URL.</summary>
    [Authorize]
    [HttpPost("checkout")]
    public async Task<IActionResult> CreateCheckout([FromBody] MpCheckoutRequest? request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PlanKey))
            return BadRequest(new { error = "planKey es requerido." });

        var tenantId = request.TenantId ?? _tenantContext.TenantId;
        if (tenantId == Guid.Empty) return BadRequest(new { error = "Tenant no resuelto." });
        if (!IsPrivileged && tenantId != _tenantContext.TenantId) return Forbid();

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return NotFound(new { error = "Tenant no encontrado." });

        var checkout = await _mp.CreatePlanCheckoutAsync(
            tenantId, request.PlanKey, request.Interval ?? "monthly", request.PayerEmail, ct);
        if (checkout is null)
            return StatusCode(502, new { error = "No se pudo crear el checkout en Mercado Pago." });

        // Record the pending plan so the webhook can apply it on approval.
        var settings = ParseSettings(tenant.ConfigJson);
        settings.Billing.Provider = "mercadopago";
        settings.Billing.PendingPlanKey = request.PlanKey.Trim().ToLowerInvariant();
        settings.Billing.PendingBillingInterval =
            string.Equals(request.Interval, "annual", StringComparison.OrdinalIgnoreCase) ? "annual" : "monthly";
        settings.Billing.PayerEmail = request.PayerEmail ?? settings.Billing.PayerEmail;
        tenant.ConfigJson = JsonSerializer.Serialize(settings, JsonOpts);
        await _db.SaveChangesAsync(ct);

        return Ok(new { initPoint = checkout.InitPoint, preferenceId = checkout.PreferenceId });
    }

    /// <summary>MP payment notifications. Payload is never trusted: the payment is re-fetched.</summary>
    [AllowAnonymous]
    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook([FromBody] JsonElement payload, CancellationToken ct)
    {
        var paymentId = ExtractPaymentId(payload);
        if (paymentId is null)
            return Accepted(new { accepted = true, ignored = true, reason = "payment id not found" });

        var payment = await _mp.GetPaymentAsync(paymentId, ct);
        if (payment is null)
            return Accepted(new { accepted = true, ignored = true, reason = "payment not resolved" });

        var parts = ParseExternalReference(payment.ExternalReference);
        if (parts is null || !Guid.TryParse(parts.Value.TenantId, out var tenantId))
            return Accepted(new { accepted = true, ignored = true, reason = "external_reference not mapped" });

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
            return Accepted(new { accepted = true, ignored = true, reason = "tenant not found" });

        var settings = ParseSettings(tenant.ConfigJson);
        settings.Billing.Provider = "mercadopago";
        settings.Billing.LastPaymentId = payment.PaymentId;
        settings.Billing.PayerEmail = payment.PayerEmail ?? settings.Billing.PayerEmail;
        settings.Billing.LastStatusDetail = payment.StatusDetail;
        settings.Billing.LastWebhookAtUtc = DateTimeOffset.UtcNow;

        if (string.Equals(payment.Status, "approved", StringComparison.OrdinalIgnoreCase))
        {
            if (Enum.TryParse<PlanTier>(parts.Value.Plan, true, out var plan))
                tenant.Plan = plan;
            tenant.SubscriptionStatus = "active";
            tenant.SubscriptionCurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(
                parts.Value.Interval == "annual" ? 365 : 30);
            settings.Billing.PendingPlanKey = null;
            settings.Billing.PendingBillingInterval = null;
        }
        else if (payment.Status is "refunded" or "charged_back" or "cancelled" or "rejected")
        {
            tenant.SubscriptionStatus = payment.Status;
        }
        // "pending"/"in_process" leave the pending plan in place.

        tenant.ConfigJson = JsonSerializer.Serialize(settings, JsonOpts);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "MercadoPago payment {PaymentId} applied to tenant {TenantId}: status={Status}, plan={Plan}",
            payment.PaymentId, tenantId, payment.Status, parts.Value.Plan);

        return Ok(new { accepted = true, tenantId, status = payment.Status });
    }

    /// <summary>Current billing state for a tenant (provider, plan, period end).</summary>
    [Authorize]
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus([FromQuery] Guid? tenantId, CancellationToken ct)
    {
        var id = tenantId ?? _tenantContext.TenantId;
        if (id == Guid.Empty) return BadRequest(new { error = "Tenant no resuelto." });
        if (!IsPrivileged && id != _tenantContext.TenantId) return Forbid();

        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();

        var settings = ParseSettings(tenant.ConfigJson);
        return Ok(new
        {
            tenantId = id,
            plan = tenant.Plan.ToString(),
            subscriptionStatus = tenant.SubscriptionStatus,
            currentPeriodEnd = tenant.SubscriptionCurrentPeriodEnd,
            provider = settings.Billing.Provider,
            pendingPlanKey = settings.Billing.PendingPlanKey,
            lastPaymentId = settings.Billing.LastPaymentId,
            payerEmail = settings.Billing.PayerEmail,
            lastWebhookAtUtc = settings.Billing.LastWebhookAtUtc
        });
    }

    private static TenantAdminSettings ParseSettings(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return new TenantAdminSettings();
        try { return JsonSerializer.Deserialize<TenantAdminSettings>(configJson, JsonOpts) ?? new TenantAdminSettings(); }
        catch { return new TenantAdminSettings(); }
    }

    /// <summary>"{tenantId}:{plan}:{interval}" → parts; null when unparseable.</summary>
    internal static (string TenantId, string Plan, string Interval)? ParseExternalReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var parts = reference.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;
        return (parts[0], parts[1].ToLowerInvariant(), parts.Length > 2 ? parts[2].ToLowerInvariant() : "monthly");
    }

    private static string? ExtractPaymentId(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        // {"data":{"id":"123"}, "type":"payment"} or {"id":"123"}
        if (payload.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("id", out var dataId))
            return dataId.ValueKind == JsonValueKind.String ? dataId.GetString()
                : dataId.ValueKind == JsonValueKind.Number ? dataId.GetRawText() : null;
        if (payload.TryGetProperty("id", out var id))
            return id.ValueKind == JsonValueKind.String ? id.GetString()
                : id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null;
        return null;
    }
}

public sealed class MpCheckoutRequest
{
    public Guid? TenantId { get; set; }
    public string? PlanKey { get; set; }
    public string? Interval { get; set; }
    public string? PayerEmail { get; set; }
}
