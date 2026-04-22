using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SaaSBot.Api.Utilities;
using SaaSBot.Application.Interfaces;
using SaaSBot.Infrastructure.Persistence;

namespace SaaSBot.Api.Services;

/// <summary>
/// Delivers signed JSON payloads to per-tenant outbound webhook URLs.
/// Payload is signed with HMAC-SHA256 using the tenant's configured secret.
/// Header: X-SaaSBot-Signature: sha256=&lt;hex&gt;
/// </summary>
public sealed class OutboundWebhookService : IOutboundWebhookService
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OutboundWebhookService> _logger;

    public OutboundWebhookService(
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<OutboundWebhookService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task SendEventAsync(Guid tenantId, string eventType, object payload, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);

        if (tenant is null) return;

        var settings = TenantSettingsJson.Parse(tenant.ConfigJson).OutboundWebhook;

        if (!settings.Enabled
            || string.IsNullOrWhiteSpace(settings.Url)
            || settings.Events.Count == 0)
            return;

        if (!settings.Events.Contains(eventType, StringComparer.OrdinalIgnoreCase))
            return;

        var envelope = new
        {
            @event = eventType,
            tenantId,
            sentAt = DateTimeOffset.UtcNow,
            data = payload
        };

        var body = JsonSerializer.Serialize(envelope, _json);
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Url);
        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-SaaSBot-Event", eventType);

        if (!string.IsNullOrWhiteSpace(settings.Secret))
        {
            var sig = ComputeHmacSha256Hex(bodyBytes, settings.Secret);
            request.Headers.Add("X-SaaSBot-Signature", $"sha256={sig}");
        }

        try
        {
            var client = _httpClientFactory.CreateClient("OutboundWebhook");
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Outbound webhook for tenant {TenantId} event {Event} returned {Status}",
                    tenantId, eventType, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Outbound webhook delivery failed for tenant {TenantId} event {Event}",
                tenantId, eventType);
        }
    }

    private static string ComputeHmacSha256Hex(byte[] payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(keyBytes, payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
