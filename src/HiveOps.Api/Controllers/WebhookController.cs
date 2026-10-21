using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using HiveOps.Application.Commands;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Models;
using HiveOps.Api.Utilities;
using HiveOps.Infrastructure.AI;
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
    private static readonly JsonSerializerOptions WebhookJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ISecretProvider _secretProvider;
    private readonly MultimodalMediaProcessor _mediaProcessor;

    public WebhookController(
        IMediator mediator,
        AppDbContext db,
        TenantContext tenantContext,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<WebhookController> logger,
        ISecretProvider secretProvider,
        MultimodalMediaProcessor mediaProcessor)
    {
        _mediator = mediator;
        _db = db;
        _tenantContext = tenantContext;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
        _secretProvider = secretProvider;
        _mediaProcessor = mediaProcessor;
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
            return await HandleInboundAsync(simplified.From, simplified.To, simplified.Text, simplified.MessageId, null, null, null, null, null, null, null, null, null, ct);
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
                meta.MediaId,
                meta.MediaMimeType,
                meta.MediaCaption,
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
                minimal.MediaId,
                minimal.MediaMimeType,
                minimal.MediaCaption,
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
        string? mediaId,
        string? mediaMimeType,
        string? mediaCaption,
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
            // Tenant isolation: processing an inbound message against the wrong
            // tenant is a cross-tenant data leak, so the first-active-tenant
            // fallback is opt-in via WhatsApp:AllowUnresolvedTenantFallback
            // (single-tenant/dev scenarios only). When disabled we still return
            // 200 so Meta does not retry the delivery.
            if (!_configuration.GetValue<bool>("WhatsApp:AllowUnresolvedTenantFallback"))
            {
                _logger.LogWarning(
                    "Webhook received but tenant could not be resolved for destinationNumber={DestNum}, phoneNumberId={PhoneId}. Message NOT processed (WhatsApp:AllowUnresolvedTenantFallback is disabled).",
                    Mask(destinationNumber), Mask(phoneNumberId));
                return Ok(new { received = true, processed = false });
            }

            _logger.LogWarning(
                "Webhook received but tenant not found for destinationNumber={DestNum}, phoneNumberId={PhoneId}. Falling back to first active tenant because WhatsApp:AllowUnresolvedTenantFallback is enabled.",
                Mask(destinationNumber), Mask(phoneNumberId));

            // Fallback: use first active tenant if available (dev/single-tenant only).
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

        // Multimodal media: download + extract content, then merge into the message text.
        // Failures never break the webhook — the original placeholder text is preserved.
        if (!string.IsNullOrWhiteSpace(mediaId))
        {
            try
            {
                var extracted = await _mediaProcessor.ProcessAsync(tenant.Id, mediaId, mediaMimeType, mediaCaption, ct);
                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    var mediaKind = mediaMimeType switch
                    {
                        { } m when m.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "imagen",
                        { } m when m.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => "audio",
                        { } m when m.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => "video",
                        _ => "documento"
                    };
                    text = $"{text}\n[Contenido extraído de {mediaKind}]: {extracted}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Media processing failed for message {MessageId}; continuing with original text.", Mask(messageId));
            }
        }

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
            ListReplyTitle = listReplyTitle,
            MediaId = mediaId,
            MediaMimeType = mediaMimeType,
            MediaCaption = mediaCaption
        };

        var response = await _mediator.Send(new IncomingMessageCommand(message), ct);
        return Ok(new { reply = response });
    }

    private bool ShouldSkipSignatureValidation()
    {
        return _environment.IsDevelopment() &&
               _configuration.GetValue<bool>("WhatsApp:SkipWebhookSignatureValidation");
    }

    /// <summary>
    /// Inbound Ticketera webhook (bidirectional ticket channel).
    /// Tenant resolution happens in <c>TenantResolutionMiddleware</c> via the
    /// <c>X-Api-Key</c> header (the /webhooks/tickets path is NOT exempt).
    /// Integrity: HMAC-SHA256 over the raw request body sent in
    /// <c>X-HiveOps-Signature: sha256=&lt;hex&gt;</c> with the shared secret
    /// <c>Ticketing:InboundSecret</c> (ISecretProvider with IConfiguration fallback).
    /// Expected payload: <see cref="TicketWebhookPayload"/> (camelCase JSON).
    /// </summary>
    [HttpPost("tickets")]
    public async Task<IActionResult> Tickets(CancellationToken ct)
    {
        if (!_tenantContext.IsResolved)
            return Unauthorized("Tenant credentials required.");

        var body = await ReadRequestBodyAsync(ct);
        if (body.Length == 0)
            return BadRequest("Invalid payload.");

        if (!ShouldSkipTicketSignatureValidation())
        {
            var inboundSecret = _configuration["Ticketing:InboundSecret"];
            try
            {
                if (_secretProvider.TryGetSecret("Ticketing:InboundSecret", out var s, out _))
                    inboundSecret = string.IsNullOrWhiteSpace(s) ? inboundSecret : s;
            }
            catch { /* fallback to configuration */ }

            if (string.IsNullOrWhiteSpace(inboundSecret))
            {
                _logger.LogWarning("Ticketing webhook rejected because Ticketing:InboundSecret is not configured.");
                return Unauthorized();
            }

            var signature = Request.Headers["X-HiveOps-Signature"].ToString();
            if (!IsValidTicketSignature(inboundSecret, body, signature))
            {
                _logger.LogWarning("Ticketing webhook rejected due to invalid or missing signature.");
                return Unauthorized();
            }
        }

        TicketWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TicketWebhookPayload>(body, WebhookJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Ticketing webhook payload could not be deserialized: {Error}", ex.Message);
            return BadRequest("Invalid payload.");
        }

        if (payload is null
            || string.IsNullOrWhiteSpace(payload.EventType)
            || string.IsNullOrWhiteSpace(payload.ExternalTicketId))
        {
            return BadRequest("Invalid payload.");
        }

        var tenantId = _tenantContext.TenantId;
        var eventType = payload.EventType.Trim();

        switch (eventType.ToLowerInvariant())
        {
            case "created":
                return await HandleTicketCreatedAsync(tenantId, payload, ct);
            case "comment":
                return await HandleTicketCommentAsync(tenantId, payload, ct);
            default:
                _logger.LogInformation(
                    "Ticketing webhook event '{EventType}' accepted as no-op. ExternalTicketId={ExternalTicketId}",
                    eventType, payload.ExternalTicketId);
                return Ok(new { received = true });
        }
    }

    private async Task<IActionResult> HandleTicketCreatedAsync(Guid tenantId, TicketWebhookPayload payload, CancellationToken ct)
    {
        var externalTicketId = payload.ExternalTicketId.Trim();

        // Idempotent: a ticket must map to a single incident.
        var existing = await _db.Incidents
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.ExternalTicketId == externalTicketId, ct);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Ticketing 'created' ignored: incident {IncidentId} already exists for ticket {ExternalTicketId}.",
                existing.Id, externalTicketId);
            return Ok(new { incidentId = existing.Id, duplicate = true });
        }

        var channelUserId = !string.IsNullOrWhiteSpace(payload.ReporterUserId)
            ? payload.ReporterUserId!.Trim()
            : externalTicketId;

        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Channel == "ticketing" && c.ChannelUserId == channelUserId, ct);

        if (conversation is null)
        {
            conversation = new Conversation
            {
                TenantId = tenantId,
                Channel = "ticketing",
                ChannelUserId = channelUserId
            };
            _db.Conversations.Add(conversation);
        }
        else
        {
            conversation.LastActivityAt = DateTimeOffset.UtcNow;
        }

        var incident = new Incident
        {
            TenantId = tenantId,
            ConversationId = conversation.Id,
            Title = string.IsNullOrWhiteSpace(payload.Title) ? $"Ticket {externalTicketId}" : payload.Title.Trim(),
            Description = payload.Description?.Trim() ?? string.Empty,
            Category = IncidentCategory.Other,
            Status = IncidentStatus.Open,
            AssignedTo = "bot",
            ExternalTicketId = externalTicketId,
            SourceChannel = "ticketing",
            UserEmail = string.IsNullOrWhiteSpace(payload.ReporterEmail) ? null : payload.ReporterEmail.Trim()
        };
        _db.Incidents.Add(incident);

        // Persist the reporter's original message so conversation history is complete.
        var initialContent = string.Join("\n",
            new[] { payload.Title, payload.Description }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(initialContent))
        {
            _db.ConversationMessages.Add(new ConversationMessage
            {
                ConversationId = conversation.Id,
                TenantId = tenantId,
                IncidentId = incident.Id,
                Role = MessageRole.User,
                Content = initialContent
            });
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Ticketing webhook created incident {IncidentId} for ticket {ExternalTicketId} (tenant {TenantId}).",
            incident.Id, externalTicketId, tenantId);

        return Ok(new { incidentId = incident.Id });
    }

    private async Task<IActionResult> HandleTicketCommentAsync(Guid tenantId, TicketWebhookPayload payload, CancellationToken ct)
    {
        var externalTicketId = payload.ExternalTicketId.Trim();

        var incident = await _db.Incidents
            .FirstOrDefaultAsync(i => i.ExternalTicketId == externalTicketId && i.TenantId == tenantId, ct);

        if (incident is null)
        {
            _logger.LogInformation(
                "Ticketing 'comment' accepted but no incident matches ticket {ExternalTicketId} (tenant {TenantId}).",
                externalTicketId, tenantId);
            return Ok(new { received = true, matched = false });
        }

        var comment = payload.CommentText?.Trim();

        // On a Resolved incident the comment itself is the user's confirmation
        // or reopen signal (the ticketing channel has no interactive buttons).
        // Reopen phrases are evaluated first because they contain confirm
        // substrings ("no se solucionó" ⊃ "se solucionó").
        if (incident.Status == IncidentStatus.Resolved && !string.IsNullOrWhiteSpace(comment))
        {
            var normalizedComment = comment.ToLowerInvariant();

            if (TicketCommentSignals.ReopenKeywords.Any(normalizedComment.Contains))
            {
                PersistTicketCommentMessage(incident, tenantId, externalTicketId, comment);
                incident.Status = IncidentStatus.Open;
                incident.AssignedTo = "bot";
                incident.Description = string.IsNullOrEmpty(incident.Description)
                    ? $"[User reopened via ticket]: {comment}"
                    : $"{incident.Description}\n\n[User reopened via ticket]: {comment}";
                incident.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Ticketing comment reopened incident {IncidentId} (ticket {ExternalTicketId}).",
                    incident.Id, externalTicketId);
                return Ok(new { resolved = "reopened" });
            }

            if (TicketCommentSignals.ConfirmKeywords.Any(normalizedComment.Contains))
            {
                PersistTicketCommentMessage(incident, tenantId, externalTicketId, comment);
                incident.Status = IncidentStatus.Closed;
                incident.ResolutionNotes = string.IsNullOrEmpty(incident.ResolutionNotes)
                    ? "[User confirmed via ticketing]"
                    : $"{incident.ResolutionNotes}\n[User confirmed via ticketing]";
                incident.EligibleForEffectivenessKpi = true;
                incident.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Ticketing comment confirmed resolution; incident {IncidentId} closed (ticket {ExternalTicketId}).",
                    incident.Id, externalTicketId);
                return Ok(new { resolved = "closed" });
            }
        }

        if (!string.IsNullOrWhiteSpace(comment))
        {
            incident.Description = string.IsNullOrEmpty(incident.Description)
                ? $"[Ticket comment]: {comment}"
                : $"{incident.Description}\n\n[Ticket comment]: {comment}";
            incident.UpdatedAt = DateTimeOffset.UtcNow;

            PersistTicketCommentMessage(incident, tenantId, externalTicketId, comment);
        }

        // A reply on a PendingInfo incident re-opens it so the workflow re-processes.
        if (incident.Status == IncidentStatus.PendingInfo)
        {
            incident.Status = IncidentStatus.Open;
            incident.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { received = true, incidentId = incident.Id });
    }

    /// <summary>
    /// Persists the ticket comment as a user <see cref="ConversationMessage"/>,
    /// creating a defensive "ticketing" conversation when the incident has none.
    /// </summary>
    private void PersistTicketCommentMessage(Incident incident, Guid tenantId, string externalTicketId, string comment)
    {
        var conversationId = incident.ConversationId;
        if (conversationId is null)
        {
            var fallbackConversation = new Conversation
            {
                TenantId = tenantId,
                Channel = "ticketing",
                ChannelUserId = externalTicketId
            };
            _db.Conversations.Add(fallbackConversation);
            incident.ConversationId = fallbackConversation.Id;
            conversationId = fallbackConversation.Id;
        }

        _db.ConversationMessages.Add(new ConversationMessage
        {
            ConversationId = conversationId.Value,
            TenantId = tenantId,
            IncidentId = incident.Id,
            Role = MessageRole.User,
            Content = comment
        });
    }

    private bool ShouldSkipTicketSignatureValidation()
    {
        return _environment.IsDevelopment() &&
               _configuration.GetValue<bool>("Ticketing:SkipWebhookSignatureValidation");
    }

    /// <summary>Accepts both "sha256=&lt;hex&gt;" and raw hex signatures.</summary>
    private static bool IsValidTicketSignature(string secret, byte[] body, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
            return false;

        var hex = signatureHeader.StartsWith("sha256=", StringComparison.Ordinal)
            ? signatureHeader["sha256=".Length..]
            : signatureHeader;

        byte[] providedSignature;
        try
        {
            providedSignature = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return false;
        }

        var computed = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(computed, providedSignature);
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

/// <summary>
/// Keyword sets used to interpret a ticket comment on a Resolved incident as a
/// user confirmation (close) or reopen signal. Matching is substring-based over
/// the lower-cased comment; reopen phrases are checked first because several of
/// them contain confirm substrings ("no se solucionó" ⊃ "se solucionó").
/// </summary>
internal static class TicketCommentSignals
{
    internal static readonly string[] ConfirmKeywords =
    [
        "confirmar",
        "confirmo",
        "ya funciona",
        "resuelto",
        "se soluciono",
        "se solucionó"
    ];

    internal static readonly string[] ReopenKeywords =
    [
        "reabrir",
        "sigue fallando",
        "no funciona",
        "no se soluciono",
        "no se solucionó",
        "persiste"
    ];
}

public sealed record WhatsAppWebhookPayload(string From, string To, string Text, string? MessageId);
public sealed record GenericWebhookPayload(string UserId, string Text, string? MessageId);

/// <summary>
/// Inbound Ticketera webhook contract. <c>EventType</c>: "created" | "comment" | other (no-op).
/// </summary>
public sealed record TicketWebhookPayload(
    string EventType,
    string ExternalTicketId,
    string? Title,
    string? Description,
    string? CommentText,
    string? ReporterUserId,
    string? ReporterEmail,
    string? ReporterPhone,
    string? Status,
    string? Priority);

internal sealed record InteractiveWebhookReply(
    string? InteractiveType,
    string? ButtonId,
    string? ButtonTitle,
    string? ListReplyId,
    string? ListReplyTitle);

internal sealed record WebhookMediaRef(
    string MediaId,
    string? MimeType,
    string? Caption,
    string MediaType);

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
                    var text = ExtractMessageText(message, out var interactiveReply, out var media);

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
                            interactiveReply?.ListReplyTitle,
                            media?.MediaId,
                            media?.MimeType,
                            media?.Caption);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    internal static string? ExtractMessageText(JsonElement message, out InteractiveWebhookReply? interactiveReply, out WebhookMediaRef? media)
    {
        interactiveReply = null;
        media = null;
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

        // Media messages (image/audio/document): extract id, mime_type and caption so the
        // caller can download and process the attachment via MultimodalMediaProcessor.
        if (type is "image" or "audio" or "document" &&
            message.TryGetProperty(type, out var mediaProp) &&
            mediaProp.ValueKind == JsonValueKind.Object)
        {
            var mediaId = mediaProp.TryGetProperty("id", out var mediaIdProp) ? SafeGetString(mediaIdProp) : null;
            var mimeType = mediaProp.TryGetProperty("mime_type", out var mimeProp) ? SafeGetString(mimeProp) : null;
            var caption = mediaProp.TryGetProperty("caption", out var captionProp) ? SafeGetString(captionProp) : null;

            if (!string.IsNullOrWhiteSpace(mediaId))
            {
                media = new WebhookMediaRef(mediaId, mimeType, caption, type);
                if (!string.IsNullOrWhiteSpace(caption))
                    return caption;

                return type switch
                {
                    "image" => "[Imagen recibida]",
                    "audio" => "[Audio recibido]",
                    _ => "[Documento recibido]"
                };
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
                                var text = ExtractMessageText(message, out var interactiveReply, out var media);

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
                                interactiveReply?.ListReplyTitle,
                                media?.MediaId,
                                media?.MimeType,
                                media?.Caption);
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
    string? ListReplyTitle,
    string? MediaId = null,
    string? MediaMimeType = null,
    string? MediaCaption = null);
internal sealed record MetaWebhookStatus(string? RecipientId, string? Status, string? MessageId, string? PhoneNumberId);
