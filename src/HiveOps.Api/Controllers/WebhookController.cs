using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text;
using HiveOps.Application.Commands;
using HiveOps.Domain.Models;
using HiveOps.Api.Utilities;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Secrets;

namespace HiveOps.Api.Controllers;

/// <summary>
/// Receives webhooks from messaging channels.
/// The TenantResolutionMiddleware exempts /webhooks paths so tenant resolution
/// happens here using the WhatsApp phone number from the request body.
/// </summary>
[ApiController]
[Route("webhooks")]
[Route("webhook")]
public sealed class WebhookController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly AppDbContext _db;
    private readonly TenantContext _tenantContext;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<WebhookController> _logger;
    private readonly ISecretProvider _secretProvider;

    public WebhookController(
        IMediator mediator,
        AppDbContext db,
        TenantContext tenantContext,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<WebhookController> logger,
        ISecretProvider secretProvider)
    {
        _mediator = mediator;
        _db = db;
        _tenantContext = tenantContext;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
        _secretProvider = secretProvider;
    }

    [HttpGet("whatsapp")]
    public IActionResult VerifyWhatsAppWebhook(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        var configuredToken = _configuration["WhatsApp:WebhookVerifyToken"];
        if (string.Equals(mode, "subscribe", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(configuredToken) &&
            string.Equals(verifyToken, configuredToken, StringComparison.Ordinal))
        {
            return Content(challenge ?? string.Empty, "text/plain");
        }

        return Unauthorized();
    }

    /// <summary>
    /// WhatsApp webhook endpoint. Accepts a simplified payload for now;
    /// replace WebhookPayload with the actual Meta / Twilio model when integrating.
    /// </summary>
    [HttpPost("whatsapp")]
    public async Task<IActionResult> WhatsApp(CancellationToken ct)
    {
        var body = await ReadRequestBodyAsync(ct);
        _logger.LogInformation("Webhook WhatsApp received on {Path}. Body bytes: {BodyLength}", Request.Path, body.Length);
        if (body.Length == 0)
            return BadRequest("Invalid payload.");

        if (!ShouldSkipSignatureValidation())
        {
            var appSecret = _configuration["WhatsApp:AppSecret"];
            try
            {
                if (_secretProvider.TryGetSecret("WhatsApp:AppSecret", out var s, out _))
                    appSecret = string.IsNullOrWhiteSpace(s) ? appSecret : s;
            }
            catch { /* fallback to configuration */ }
            if (string.IsNullOrWhiteSpace(appSecret))
            {
                _logger.LogWarning("WhatsApp webhook rejected because AppSecret is not configured.");
                return Unauthorized();
            }

            var signature = Request.Headers["X-Hub-Signature-256"].ToString();
            if (!MetaWebhookSignatureVerifier.IsValid(appSecret, body, signature))
            {
                _logger.LogWarning("WhatsApp webhook rejected due to invalid or missing signature.");
                return Unauthorized();
            }
        }

        using var document = JsonDocument.Parse(body);
        var payload = document.RootElement;

        if (WhatsAppWebhookParsing.TryParseSimplifiedPayload(payload, out var simplified))
        {
            _logger.LogInformation("Webhook parsed as simplified payload. From={From} To={To} MessageId={MessageId}", Mask(simplified.From), Mask(simplified.To), Mask(simplified.MessageId));
            return await HandleInboundAsync(simplified.From, simplified.To, simplified.Text, simplified.MessageId, null, null, null, null, null, null, ct);
        }

        if (WhatsAppWebhookParsing.TryParseMetaPayload(payload, out var meta))
        {
            _logger.LogInformation("Webhook parsed as Meta payload. From={From} DisplayPhone={DisplayPhone} PhoneNumberId={PhoneNumberId} MessageId={MessageId}",
                Mask(meta.From), Mask(meta.DisplayPhoneNumber), Mask(meta.PhoneNumberId), Mask(meta.MessageId));
            return await HandleInboundAsync(
                meta.From,
                meta.DisplayPhoneNumber,
                meta.Text,
                meta.MessageId,
                meta.PhoneNumberId,
                meta.InteractiveType,
                meta.ButtonId,
                meta.ButtonTitle,
                meta.ListReplyId,
                meta.ListReplyTitle,
                ct);
        }

        if (WhatsAppWebhookParsing.TryParseMetaStatusPayload(payload, out var statusUpdate))
        {
            _logger.LogInformation(
                "Webhook received as WhatsApp status update. Recipient={RecipientId} Status={Status} PhoneNumberId={PhoneNumberId} MessageId={MessageId}",
                Mask(statusUpdate.RecipientId),
                statusUpdate.Status,
                Mask(statusUpdate.PhoneNumberId),
                Mask(statusUpdate.MessageId));
            return Ok(new { received = true, status = true });
        }

        // Fallback: Extract minimal data from Meta payload even if structure is incomplete
        // Bot always responds, never ignores a message
        if (WhatsAppWebhookParsing.TryExtractMinimalMetaPayload(payload, out var minimal))
        {
            _logger.LogInformation("Webhook parsed as minimal Meta payload. From={From} DisplayPhone={DisplayPhone} PhoneNumberId={PhoneNumberId} MessageId={MessageId}",
                Mask(minimal.From), Mask(minimal.DisplayPhoneNumber), Mask(minimal.PhoneNumberId), Mask(minimal.MessageId));
            return await HandleInboundAsync(
                minimal.From,
                minimal.DisplayPhoneNumber,
                minimal.Text,
                minimal.MessageId,
                minimal.PhoneNumberId,
                minimal.InteractiveType,
                minimal.ButtonId,
                minimal.ButtonTitle,
                minimal.ListReplyId,
                minimal.ListReplyTitle,
                ct);
        }

        _logger.LogWarning("WhatsApp webhook payload could not be parsed. Raw omitted for privacy. Length={Len}", payload.GetRawText().Length);
        // Bot introduces itself even when payload is malformed
        return Ok(new { received = true, message = "Webhook received but format not recognized. Bot should still respond." });
    }

    private async Task<IActionResult> HandleInboundAsync(
        string from,
        string? destinationNumber,
        string text,
        string? messageId,
        string? phoneNumberId,
        string? interactiveType,
        string? buttonId,
        string? buttonTitle,
        string? listReplyId,
        string? listReplyTitle,
        CancellationToken ct)
    {
        // Bot always processes webhooks, never ignores. Use defaults if critical fields missing.
        if (string.IsNullOrWhiteSpace(from))
        {
            from = $"unknown_{Guid.NewGuid().ToString().Substring(0, 8)}";
            _logger.LogWarning("Webhook missing 'from' field. Generated pseudo-id: {PseudoId}", from);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            text = "[Mensaje sin contenido de texto]";
            _logger.LogWarning("Webhook missing text content. Using placeholder.");
        }

        // Resolve tenant from the WhatsApp destination number (the business number)
        var tenants = await _db.Tenants
            .Where(t => t.IsActive)
            .ToListAsync(ct);

        var tenant = tenants.FirstOrDefault(t =>
        {
            if (WhatsAppWebhookParsing.SamePhone(t.WhatsAppNumber, destinationNumber))
                return true;

            var settings = TenantSettingsJson.Parse(t.ConfigJson).WhatsApp;
            return !string.IsNullOrWhiteSpace(phoneNumberId) &&
                   string.Equals(settings.PhoneNumberId, phoneNumberId, StringComparison.Ordinal);
        });

        if (tenant is null)
        {
            _logger.LogWarning("Webhook received but tenant not found for destinationNumber={DestNum}, phoneNumberId={PhoneId}. Will attempt generic tenant processing.", Mask(destinationNumber), Mask(phoneNumberId));
            
            // Fallback: Use first active tenant if available (for testing/single-tenant scenarios)
            tenant = tenants.FirstOrDefault();
            if (tenant is null)
                return NotFound("No active tenants found to process webhook.");
        }

        if (!string.IsNullOrWhiteSpace(phoneNumberId))
        {
            var settings = TenantSettingsJson.Parse(tenant.ConfigJson);
            if (!string.Equals(settings.WhatsApp.PhoneNumberId, phoneNumberId, StringComparison.Ordinal))
            {
                settings.WhatsApp.PhoneNumberId = phoneNumberId;
                settings.WhatsApp.PhoneNumber = destinationNumber ?? settings.WhatsApp.PhoneNumber;
                settings.WhatsApp.Enabled = !string.IsNullOrWhiteSpace(settings.WhatsApp.ApiKey);
                tenant.ConfigJson = TenantSettingsJson.Stringify(settings);
                await _db.SaveChangesAsync(ct);
            }
        }

        _tenantContext.SetTenant(tenant.Id);
        _logger.LogInformation("Webhook tenant resolved. TenantId={TenantId} From={From} NormalizedFrom={NormalizedFrom} MessageId={MessageId}",
            tenant.Id, Mask(from), Mask(WhatsAppWebhookParsing.NormalizeSender(from)), Mask(messageId));

        var message = new IncomingMessage
        {
            Channel = "whatsapp",
            ChannelUserId = WhatsAppWebhookParsing.NormalizeSender(from),
            PhoneNumber = WhatsAppWebhookParsing.NormalizeSender(from),
            Text = text,
            ExternalMessageId = messageId,
            InteractiveType = interactiveType,
            ButtonId = buttonId,
            ButtonTitle = buttonTitle,
            ListReplyId = listReplyId,
            ListReplyTitle = listReplyTitle
        };

        var response = await _mediator.Send(new IncomingMessageCommand(message), ct);
        return Ok(new { reply = response });
    }

    private bool ShouldSkipSignatureValidation()
    {
        return _environment.IsDevelopment() &&
               _configuration.GetValue<bool>("WhatsApp:SkipWebhookSignatureValidation");
    }

    private async Task<byte[]> ReadRequestBodyAsync(CancellationToken ct)
    {
        await using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var v = new string(value.Where(char.IsLetterOrDigit).ToArray());
        if (v.Length <= 4) return "****";
        return new string('*', Math.Max(0, v.Length - 4)) + v[^4..];
    }

    /// <summary>Generic channel webhook for non-WhatsApp integrations (requires X-Api-Key header).</summary>
    [HttpPost("{channelType}")]
    public async Task<IActionResult> GenericChannel(
        [FromRoute] string channelType,
        [FromBody] GenericWebhookPayload payload,
        CancellationToken ct)
    {
        // TenantContext already resolved by middleware via X-Api-Key
        if (!_tenantContext.IsResolved)
            return Unauthorized("Tenant credentials required.");

        if (payload is null || string.IsNullOrWhiteSpace(payload.UserId) || string.IsNullOrWhiteSpace(payload.Text))
            return BadRequest("Invalid payload.");

        var message = new IncomingMessage
        {
            Channel = channelType.ToLowerInvariant(),
            ChannelUserId = payload.UserId,
            Text = payload.Text,
            ExternalMessageId = payload.MessageId
        };

        var response = await _mediator.Send(new IncomingMessageCommand(message), ct);
        return Ok(new { reply = response });
    }
}

public sealed record WhatsAppWebhookPayload(string From, string To, string Text, string? MessageId);
public sealed record GenericWebhookPayload(string UserId, string Text, string? MessageId);

internal sealed record InteractiveWebhookReply(
    string? InteractiveType,
    string? ButtonId,
    string? ButtonTitle,
    string? ListReplyId,
    string? ListReplyTitle);

internal static class WhatsAppWebhookParsing
{
    internal static bool TryParseSimplifiedPayload(JsonElement payload, out WhatsAppWebhookPayload parsed)
    {
        parsed = new WhatsAppWebhookPayload(string.Empty, string.Empty, string.Empty, null);
        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        if (!payload.TryGetProperty("from", out var fromProp) ||
            !payload.TryGetProperty("text", out var textProp))
            return false;

        var to = payload.TryGetProperty("to", out var toProp) ? toProp.GetString() : null;
        var messageId = payload.TryGetProperty("messageId", out var messageIdProp) ? messageIdProp.GetString() : null;
        parsed = new WhatsAppWebhookPayload(fromProp.GetString() ?? string.Empty, to ?? string.Empty, textProp.GetString() ?? string.Empty, messageId);
        return true;
    }

    internal static bool TryParseMetaPayload(JsonElement payload, out MetaWebhookMessage parsed)
    {
        parsed = default!;
        if (!payload.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value))
                    continue;

                var displayPhoneNumber = value.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("display_phone_number", out var displayPhoneProp)
                    ? displayPhoneProp.GetString()
                    : null;
                var phoneNumberId = value.TryGetProperty("metadata", out metadata) && metadata.TryGetProperty("phone_number_id", out var phoneIdProp)
                    ? phoneIdProp.GetString()
                    : null;

                if (!value.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var message in messages.EnumerateArray())
                {
                    var from = message.TryGetProperty("from", out var fromProp) ? fromProp.GetString() : null;
                    var messageId = message.TryGetProperty("id", out var messageIdProp) ? messageIdProp.GetString() : null;
                    var text = ExtractMessageText(message, out var interactiveReply);

                    if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(text))
                    {
                        parsed = new MetaWebhookMessage(
                            from!,
                            displayPhoneNumber,
                            phoneNumberId,
                            text!,
                            messageId,
                            interactiveReply?.InteractiveType,
                            interactiveReply?.ButtonId,
                            interactiveReply?.ButtonTitle,
                            interactiveReply?.ListReplyId,
                            interactiveReply?.ListReplyTitle);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    internal static string? ExtractMessageText(JsonElement message, out InteractiveWebhookReply? interactiveReply)
    {
        interactiveReply = null;
        var type = message.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

        if (type == "interactive" && message.TryGetProperty("interactive", out var interactiveProp))
        {
            if (interactiveProp.TryGetProperty("button_reply", out var buttonReplyProp))
            {
                var id = buttonReplyProp.TryGetProperty("id", out var idProp) ? SafeGetString(idProp) : null;
                var title = buttonReplyProp.TryGetProperty("title", out var titleProp) ? SafeGetString(titleProp) : null;
                interactiveReply = new InteractiveWebhookReply("button", id, title, null, null);
                return title ?? id;
            }

            if (interactiveProp.TryGetProperty("list_reply", out var listReplyProp))
            {
                var id = listReplyProp.TryGetProperty("id", out var idProp) ? SafeGetString(idProp) : null;
                var title = listReplyProp.TryGetProperty("title", out var titleProp) ? SafeGetString(titleProp) : null;
                interactiveReply = new InteractiveWebhookReply("list", null, null, id, title);
                return title ?? id;
            }
        }

        var text = type switch
        {
            "text" when message.TryGetProperty("text", out var textProp) && textProp.TryGetProperty("body", out var bodyProp) => SafeGetString(bodyProp),
            "button" when message.TryGetProperty("button", out var buttonProp) && buttonProp.TryGetProperty("text", out var buttonTextProp) => SafeGetString(buttonTextProp),
            "image" => "[Imagen]",
            "audio" => "[Audio]",
            "video" => "[Video]",
            "document" => "[Documento]",
            "location" => "[Ubicación]",
            "reaction" => "[Reacción]",
            _ => null
        };
        return text;
    }

    private static string? SafeGetString(JsonElement element)
    {
        try
        {
            return element.GetString();
        }
        catch (InvalidOperationException)
        {
            // Invalid UTF-8 or non-string token should not break webhook processing.
            return null;
        }
    }

    /// <summary>
    /// Fallback parser: Extract minimal viable payload from Meta format even if structure is incomplete.
    /// This ensures bot responds even to malformed webhooks.
    /// Strategy:
    /// 1. Try to find any message with 'from' and text across all entries/changes
    /// 2. If no message found, try other malformed message shapes
    /// 3. Extract metadata (displayPhoneNumber, phoneNumberId) if available
    /// 4. Accept minimal data to trigger bot response (even generic "Hola")
    /// </summary>
    internal static bool TryExtractMinimalMetaPayload(JsonElement payload, out MetaWebhookMessage parsed)
    {
        parsed = default!;
        if (!payload.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return false;

        string? displayPhoneNumber = null;
        string? phoneNumberId = null;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value))
                    continue;

                // Extract metadata if available
                if (value.TryGetProperty("metadata", out var metadata))
                {
                    if (metadata.TryGetProperty("display_phone_number", out var displayPhoneProp))
                        displayPhoneNumber = displayPhoneProp.GetString();
                    if (metadata.TryGetProperty("phone_number_id", out var phoneIdProp))
                        phoneNumberId = phoneIdProp.GetString();
                }

                // Try messages first
                if (value.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                {
                    foreach (var message in messages.EnumerateArray())
                    {
                        var from = message.TryGetProperty("from", out var fromProp) ? fromProp.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(from))
                        {
                            var messageId = message.TryGetProperty("id", out var messageIdProp) ? messageIdProp.GetString() : null;
                                var text = ExtractMessageText(message, out var interactiveReply);
                            
                            // If no structured text, try to extract raw message content or use generic greeting
                            if (string.IsNullOrWhiteSpace(text))
                            {
                                // Fallback: look for any text field
                                if (message.TryGetProperty("text", out var fallbackText) && fallbackText.ValueKind == JsonValueKind.Object)
                                {
                                    if (fallbackText.TryGetProperty("body", out var bodyFallback))
                                        text = bodyFallback.GetString();
                                }
                                // If still no text, use generic acknowledgment so bot always responds
                                text = text ?? "[Mensaje sin texto]";
                            }

                            parsed = new MetaWebhookMessage(
                                from!,
                                displayPhoneNumber,
                                phoneNumberId,
                                text!,
                                messageId,
                                interactiveReply?.InteractiveType,
                                interactiveReply?.ButtonId,
                                interactiveReply?.ButtonTitle,
                                interactiveReply?.ListReplyId,
                                interactiveReply?.ListReplyTitle);
                            return true;
                        }
                    }
                }

            }
        }

        return false;
    }

    internal static bool TryParseMetaStatusPayload(JsonElement payload, out MetaWebhookStatus parsed)
    {
        parsed = default!;
        if (!payload.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value))
                    continue;

                var phoneNumberId = value.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("phone_number_id", out var phoneIdProp)
                    ? phoneIdProp.GetString()
                    : null;

                if (!value.TryGetProperty("statuses", out var statuses) || statuses.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var status in statuses.EnumerateArray())
                {
                    var recipientId = status.TryGetProperty("recipient_id", out var recipientProp) ? recipientProp.GetString() : null;
                    var statusText = status.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;
                    var statusId = status.TryGetProperty("id", out var statusIdProp) ? statusIdProp.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(recipientId) || !string.IsNullOrWhiteSpace(statusText) || !string.IsNullOrWhiteSpace(statusId))
                    {
                        parsed = new MetaWebhookStatus(recipientId, statusText, statusId, phoneNumberId);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    internal static string NormalizeSender(string from)
    {
        return new string(from.Where(ch => char.IsDigit(ch) || ch == '+').ToArray());
    }

    internal static bool SamePhone(string? left, string? right)
    {
        static string Normalize(string? phone) => new(phone?.Where(char.IsDigit).ToArray() ?? []);
        return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && Normalize(left) == Normalize(right);
    }
}

internal sealed record MetaWebhookMessage(
    string From,
    string? DisplayPhoneNumber,
    string? PhoneNumberId,
    string Text,
    string? MessageId,
    string? InteractiveType,
    string? ButtonId,
    string? ButtonTitle,
    string? ListReplyId,
    string? ListReplyTitle);
internal sealed record MetaWebhookStatus(string? RecipientId, string? Status, string? MessageId, string? PhoneNumberId);
