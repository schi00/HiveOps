using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using HiveOps.Api.Utilities;
using HiveOps.Application.Interfaces;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Api.Services;

/// <summary>
/// Delivers signed JSON payloads to per-tenant outbound webhook URLs.
/// Payload is signed with HMAC-SHA256 using the tenant's configured secret.
/// Header: X-HiveOps-Signature: sha256=&lt;hex&gt;
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

        var bodyObject = BuildBodyForTarget(settings.Url, eventType, tenantId, payload);
        var body = JsonSerializer.Serialize(bodyObject, _json);
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Url);
        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-HiveOps-Event", eventType);

        if (!string.IsNullOrWhiteSpace(settings.Secret))
        {
            var sig = ComputeHmacSha256Hex(bodyBytes, settings.Secret);
            request.Headers.Add("X-HiveOps-Signature", $"sha256={sig}");
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

    private static object BuildBodyForTarget(string url, string eventType, Guid tenantId, object payload)
    {
        if (IsSlackWebhook(url))
            return BuildSlackBody(eventType, tenantId, payload);

        if (IsTeamsWebhook(url))
            return BuildTeamsBody(eventType, tenantId, payload);

        return new
        {
            @event = eventType,
            tenantId,
            sentAt = DateTimeOffset.UtcNow,
            data = payload
        };
    }

    private static bool IsSlackWebhook(string url)
        => url.Contains("hooks.slack.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsTeamsWebhook(string url)
        => url.Contains("webhook.office.com", StringComparison.OrdinalIgnoreCase)
           || url.Contains(".logic.azure.com", StringComparison.OrdinalIgnoreCase);

    private static object BuildSlackBody(string eventType, Guid tenantId, object payload)
    {
        var el = JsonSerializer.SerializeToElement(payload, _json);
        var incidentId = TryGetString(el, "incidentId");
        var title = TryGetString(el, "title") ?? $"HiveOps Event: {eventType}";
        var severity = TryGetString(el, "severity");
        var branchName = TryGetString(el, "branchName");
        var approveLink = TryGetString(el, "approveUrl") ?? TryGetString(el, "approvePath");
        var rejectLink = TryGetString(el, "rejectUrl") ?? TryGetString(el, "rejectPath");

        var summaryText = $"[HiveOps] {eventType} (Tenant {tenantId})"
            + (incidentId is not null ? $" | Incident: {incidentId}" : string.Empty)
            + (branchName is not null ? $" | Branch: {branchName}" : string.Empty)
            + (approveLink is not null ? $" | Approve: {approveLink} | Reject: {rejectLink}" : string.Empty);

        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("slackBlocks", out var existingBlocks) && existingBlocks.ValueKind == JsonValueKind.Array)
        {
            return new
            {
                text = summaryText,
                blocks = existingBlocks
            };
        }

        var blocks = new List<object>
        {
            new
            {
                type = "header",
                text = new { type = "plain_text", text = title }
            },
            new
            {
                type = "section",
                text = new
                {
                    type = "mrkdwn",
                    text = $"*Event:* `{eventType}`\n*Tenant:* `{tenantId}`"
                        + (incidentId is not null ? $"\n*Incident:* `{incidentId}`" : string.Empty)
                        + (severity is not null ? $"\n*Severity:* `{severity}`" : string.Empty)
                        + (branchName is not null ? $"\n*Branch:* `{branchName}`" : string.Empty)
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(approveLink) && !string.IsNullOrWhiteSpace(rejectLink))
        {
            blocks.Add(new
            {
                type = "actions",
                elements = new object[]
                {
                    new
                    {
                        type = "button",
                        style = "primary",
                        text = new { type = "plain_text", text = "Aprobar Fix (1-Click)" },
                        url = approveLink
                    },
                    new
                    {
                        type = "button",
                        style = "danger",
                        text = new { type = "plain_text", text = "Rechazar Fix" },
                        url = rejectLink
                    }
                }
            });
        }

        return new
        {
            text = summaryText,
            blocks
        };
    }

    private static object BuildTeamsBody(string eventType, Guid tenantId, object payload)
    {
        var el = JsonSerializer.SerializeToElement(payload, _json);
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("teamsCard", out var existingCard) && existingCard.ValueKind == JsonValueKind.Object)
        {
            return existingCard;
        }

        var incidentId = TryGetString(el, "incidentId") ?? "N/A";
        var title = TryGetString(el, "title") ?? $"HiveOps — {eventType}";
        var severity = TryGetString(el, "severity") ?? "Normal";
        var branchName = TryGetString(el, "branchName") ?? "N/A";
        var approveLink = TryGetString(el, "approveUrl") ?? TryGetString(el, "approvePath");
        var rejectLink = TryGetString(el, "rejectUrl") ?? TryGetString(el, "rejectPath");

        var actions = new List<object>();
        if (!string.IsNullOrWhiteSpace(approveLink))
            actions.Add(new { type = "Action.OpenUrl", title = "Aprobar Fix (1-Click)", url = approveLink });
        if (!string.IsNullOrWhiteSpace(rejectLink))
            actions.Add(new { type = "Action.OpenUrl", title = "Rechazar Fix", url = rejectLink });

        return new
        {
            type = "message",
            attachments = new object[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.4",
                        body = new object[]
                        {
                            new { type = "TextBlock", size = "Medium", weight = "Bolder", text = title },
                            new
                            {
                                type = "FactSet",
                                facts = new object[]
                                {
                                    new { title = "Event", value = eventType },
                                    new { title = "Tenant ID", value = tenantId.ToString() },
                                    new { title = "Incident ID", value = incidentId },
                                    new { title = "Severity", value = severity },
                                    new { title = "Branch", value = branchName }
                                }
                            }
                        },
                        actions
                    }
                }
            }
        };
    }

    private static string? TryGetString(JsonElement el, string propName)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        if (!el.TryGetProperty(propName, out var prop))
            return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.ToString(),
            _ => null
        };
    }

    private static string ComputeHmacSha256Hex(byte[] payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(keyBytes, payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
