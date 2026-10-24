using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using HiveOps.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HiveOps.Infrastructure.Billing;

/// <summary>
/// Mercado Pago billing via Checkout Pro preferences. A per-preference
/// notification_url routes webhooks to this deployment, independent of the
/// app-level webhook configured for other products on the same MP account.
/// Plan prices are configured in USD and charged in Billing:ChargeCurrency
/// using Billing:UsdToArsRate.
/// </summary>
public sealed class MercadoPagoService : IMercadoPagoService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly MercadoPagoOptions _options;
    private readonly ILogger<MercadoPagoService> _logger;

    public MercadoPagoService(
        HttpClient http,
        IConfiguration configuration,
        IOptions<MercadoPagoOptions> options,
        ILogger<MercadoPagoService> logger)
    {
        _http = http;
        _configuration = configuration;
        _options = options.Value;
        _logger = logger;
    }

    public static (decimal MonthlyUsd, decimal AnnualUsd)? GetPlanPriceUsd(IConfiguration config, string planKey)
    {
        decimal ReadUsd(string suffix, decimal fallback) =>
            decimal.TryParse(config[$"Billing:Plans:{planKey}:{suffix}"], NumberStyles.Number,
                CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

        return planKey.Trim().ToLowerInvariant() switch
        {
            "starter" => (ReadUsd("MonthlyUsd", 200m), ReadUsd("AnnualUsd", 1800m)),
            "pro" => (ReadUsd("MonthlyUsd", 499m), ReadUsd("AnnualUsd", 4190m)),
            "enterprise" => (ReadUsd("MonthlyUsd", 1499m), ReadUsd("AnnualUsd", 14990m)),
            _ => null
        };
    }

    public async Task<MercadoPagoCheckout?> CreatePlanCheckoutAsync(
        Guid tenantId, string planKey, string billingInterval, string? payerEmail, CancellationToken ct)
    {
        var prices = GetPlanPriceUsd(_configuration, planKey);
        if (prices is null)
        {
            _logger.LogWarning("MercadoPago checkout requested for unknown plan '{PlanKey}'.", planKey);
            return null;
        }

        var interval = string.Equals(billingInterval, "annual", StringComparison.OrdinalIgnoreCase)
            ? "annual" : "monthly";
        var usdAmount = interval == "annual" ? prices.Value.AnnualUsd : prices.Value.MonthlyUsd;

        var rateRaw = _configuration["Billing:UsdToArsRate"];
        if (!decimal.TryParse(rateRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) || rate <= 0)
        {
            _logger.LogWarning("Billing:UsdToArsRate not configured; cannot create MercadoPago checkout.");
            return null;
        }
        var chargeCurrency = _configuration["Billing:ChargeCurrency"];
        if (string.IsNullOrWhiteSpace(chargeCurrency)) chargeCurrency = "ARS";
        var amount = Math.Round(usdAmount * rate, 2);

        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            _logger.LogWarning("MercadoPago:AccessToken is not configured; cannot create checkout.");
            return null;
        }

        var baseUrl = _options.ApiBaseUrl.TrimEnd('/');
        var notificationUrl = $"{_options.NotificationBaseUrl.TrimEnd('/')}/api/billing/mercadopago/webhook";

        var body = new Dictionary<string, object?>
        {
            ["items"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["title"] = $"HiveOps plan {planKey} ({interval})",
                    ["quantity"] = 1,
                    ["unit_price"] = amount,
                    ["currency_id"] = chargeCurrency
                }
            },
            // "{tenantId}:{plan}:{interval}" — the webhook resolves tenant+plan from it.
            ["external_reference"] = $"{tenantId}:{planKey.Trim().ToLowerInvariant()}:{interval}",
            ["payer"] = payerEmail is null ? null : new Dictionary<string, object?> { ["email"] = payerEmail },
            ["notification_url"] = notificationUrl,
            ["back_urls"] = new Dictionary<string, object?>
            {
                ["success"] = _options.BackUrl,
                ["pending"] = _options.BackUrl,
                ["failure"] = _options.BackUrl
            },
            ["auto_return"] = "approved"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/checkout/preferences")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

        using var response = await _http.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("MercadoPago preference creation failed. StatusCode={StatusCode} Body={Body}",
                response.StatusCode, content is { Length: > 300 } ? content[..300] : content);
            return null;
        }

        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;
        var initPoint = root.TryGetProperty("init_point", out var ip) && ip.ValueKind == JsonValueKind.String
            ? ip.GetString() : null;
        var prefId = root.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.String
            ? pid.GetString() : null;
        return initPoint is null ? null : new MercadoPagoCheckout(initPoint, prefId);
    }

    public async Task<MercadoPagoPaymentSnapshot?> GetPaymentAsync(string paymentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(paymentId) || string.IsNullOrWhiteSpace(_options.AccessToken))
            return null;

        var baseUrl = _options.ApiBaseUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/payments/{paymentId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("MercadoPago payment lookup failed for {PaymentId}. StatusCode={StatusCode}",
                paymentId, response.StatusCode);
            return null;
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;

        string? Str(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        decimal Dec(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : 0m;
        string? PayerEmail() =>
            root.TryGetProperty("payer", out var p) && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

        return new MercadoPagoPaymentSnapshot(
            Str("id") ?? paymentId,
            Str("status") ?? "unknown",
            Str("status_detail"),
            Str("external_reference"),
            PayerEmail(),
            Dec("transaction_amount"),
            Str("currency_id"));
    }
}
