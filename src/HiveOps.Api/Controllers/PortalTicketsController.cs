using System.Security.Cryptography;
using System.Text;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace HiveOps.Api.Controllers;

/// <summary>
/// Hosted ticketing portal: reporter-facing endpoints so tenant customers can
/// open and track incidents without WhatsApp or an external ticketing system.
/// Tenant is resolved from the route GUID; ticket access is gated by an
/// HMAC tracking token minted at creation (Ticketing:PortalSecret, falling
/// back to Admin:ApiKey). /api/portal is exempt from TenantResolutionMiddleware.
/// </summary>
[ApiController]
[Route("api/portal/tickets")]
public sealed class PortalTicketsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TenantContext _tenantContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PortalTicketsController> _logger;

    public PortalTicketsController(
        AppDbContext db,
        TenantContext tenantContext,
        IConfiguration configuration,
        ILogger<PortalTicketsController> logger)
    {
        _db = db;
        _tenantContext = tenantContext;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Creates a ticket (= incident on the hosted "ticketing" channel).</summary>
    [HttpPost("{tenantId:guid}")]
    [EnableRateLimiting("onboarding-policy")]
    public async Task<IActionResult> CreateTicket(
        [FromRoute] Guid tenantId,
        [FromBody] CreatePortalTicketRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { error = "Title is required." });
        if (request.Title.Length > 300)
            return BadRequest(new { error = "Title is too long." });
        if (request.Description is { Length: > 8000 })
            return BadRequest(new { error = "Description is too long." });

        if (!await ActivateTenantAsync(tenantId, ct))
            return NotFound(new { error = "Portal not found." });

        var secret = ResolvePortalSecret();
        if (secret is null)
        {
            _logger.LogWarning("Ticket portal rejected: Ticketing:PortalSecret/Admin:ApiKey not configured.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Ticketing portal is not enabled." });
        }

        var ticketId = await GenerateTicketIdAsync(ct);

        var conversation = new Conversation
        {
            TenantId = tenantId,
            Channel = "ticketing",
            ChannelUserId = ticketId
        };
        _db.Conversations.Add(conversation);

        var incident = new Incident
        {
            TenantId = tenantId,
            ConversationId = conversation.Id,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            Category = IncidentCategory.Other,
            Status = IncidentStatus.Open,
            AssignedTo = "bot",
            ExternalTicketId = ticketId,
            SourceChannel = "ticketing",
            UserEmail = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim()
        };
        _db.Incidents.Add(incident);

        var initialContent = string.Join("\n",
            new[] { request.Title, request.Description }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        _db.ConversationMessages.Add(new ConversationMessage
        {
            ConversationId = conversation.Id,
            TenantId = tenantId,
            IncidentId = incident.Id,
            Role = MessageRole.User,
            Content = string.IsNullOrWhiteSpace(request.Name)
                ? initialContent
                : $"[{request.Name.Trim()}]\n{initialContent}"
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Portal ticket {TicketId} created for tenant {TenantId} (incident {IncidentId}).",
            ticketId, tenantId, incident.Id);

        var token = ComputeToken(secret, tenantId, ticketId);
        return Ok(new PortalTicketCreatedResponse(
            ticketId,
            token,
            incident.Status.ToString(),
            $"/api/portal/tickets/{tenantId:D}/{ticketId}?token={token}"));
    }

    /// <summary>Returns ticket status and the public thread (user + assistant messages).</summary>
    [HttpGet("{tenantId:guid}/{ticketId}")]
    public async Task<IActionResult> GetTicket(
        [FromRoute] Guid tenantId,
        [FromRoute] string ticketId,
        [FromQuery] string? token,
        CancellationToken ct)
    {
        var incident = await LoadAuthorizedIncidentAsync(tenantId, ticketId, token, ct);
        if (incident is null)
            return NotFound(new { error = "Ticket not found." });

        var messages = await _db.ConversationMessages
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId
                && m.IncidentId == incident.Id
                && (m.Role == MessageRole.User || m.Role == MessageRole.Assistant))
            .OrderBy(m => m.CreatedAt)
            .Select(m => new PortalTicketMessage(
                m.Role == MessageRole.User ? "user" : "assistant",
                m.Content,
                m.CreatedAt))
            .ToListAsync(ct);

        return Ok(new PortalTicketResponse(
            incident.ExternalTicketId!,
            incident.Title,
            incident.Status.ToString(),
            incident.CreatedAt,
            incident.UpdatedAt,
            incident.ResolvedAt,
            messages));
    }

    /// <summary>Reporter comment: mirrors the inbound ticketing webhook semantics
    /// (reopen on Resolved+reopen keyword, close on Resolved+confirm keyword,
    /// re-open PendingInfo) and persists the message on the incident thread.</summary>
    [HttpPost("{tenantId:guid}/{ticketId}/comments")]
    [EnableRateLimiting("onboarding-policy")]
    public async Task<IActionResult> AddComment(
        [FromRoute] Guid tenantId,
        [FromRoute] string ticketId,
        [FromBody] PortalTicketCommentRequest request,
        [FromQuery] string? token,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 4000)
            return BadRequest(new { error = "Comment text is required (max 4000 chars)." });

        var incident = await LoadAuthorizedIncidentAsync(tenantId, ticketId, token, ct);
        if (incident is null)
            return NotFound(new { error = "Ticket not found." });

        var comment = request.Text.Trim();
        var normalized = comment.ToLowerInvariant();

        if (incident.Status == IncidentStatus.Resolved)
        {
            if (TicketCommentSignals.ReopenKeywords.Any(normalized.Contains))
            {
                PersistComment(incident, tenantId, comment);
                incident.Status = IncidentStatus.Open;
                incident.AssignedTo = "bot";
                incident.Description = string.IsNullOrEmpty(incident.Description)
                    ? $"[User reopened via portal]: {comment}"
                    : $"{incident.Description}\n\n[User reopened via portal]: {comment}";
                incident.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                return Ok(new { resolved = "reopened" });
            }

            if (TicketCommentSignals.ConfirmKeywords.Any(normalized.Contains))
            {
                PersistComment(incident, tenantId, comment);
                incident.Status = IncidentStatus.Closed;
                incident.ResolutionNotes = string.IsNullOrEmpty(incident.ResolutionNotes)
                    ? "[User confirmed via portal]"
                    : $"{incident.ResolutionNotes}\n[User confirmed via portal]";
                incident.EligibleForEffectivenessKpi = true;
                incident.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                return Ok(new { resolved = "closed" });
            }
        }

        incident.Description = string.IsNullOrEmpty(incident.Description)
            ? $"[Portal comment]: {comment}"
            : $"{incident.Description}\n\n[Portal comment]: {comment}";
        incident.UpdatedAt = DateTimeOffset.UtcNow;

        PersistComment(incident, tenantId, comment);

        if (incident.Status == IncidentStatus.PendingInfo)
        {
            incident.Status = IncidentStatus.Open;
            incident.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (incident.Status == IncidentStatus.Closed)
        {
            // Comments on a closed ticket re-open it as a new bot follow-up.
            incident.Status = IncidentStatus.Open;
            incident.AssignedTo = "bot";
            incident.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { received = true, status = incident.Status.ToString() });
    }

    // -- helpers ---------------------------------------------------------------

    /// <summary>Sets tenant context for the request. Returns false when the
    /// tenant does not exist or is inactive — or when middleware already
    /// resolved a different tenant.</summary>
    private async Task<bool> ActivateTenantAsync(Guid tenantId, CancellationToken ct)
    {
        // Tenants has no RLS — safe to query before SetTenant.
        var exists = await _db.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Id == tenantId && t.IsActive, ct);
        if (!exists)
            return false;

        if (_tenantContext.IsResolved)
            return _tenantContext.TenantId == tenantId;

        _tenantContext.SetTenant(tenantId);
        return true;
    }

    private string? ResolvePortalSecret()
    {
        var secret = _configuration["Ticketing:PortalSecret"];
        if (!string.IsNullOrWhiteSpace(secret))
            return secret;
        secret = _configuration["Admin:ApiKey"];
        return string.IsNullOrWhiteSpace(secret) ? null : secret;
    }

    private static string ComputeToken(string secret, Guid tenantId, string ticketId)
    {
        var bytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"portal:{tenantId:D}:{ticketId.ToUpperInvariant()}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task<Incident?> LoadAuthorizedIncidentAsync(
        Guid tenantId, string ticketId, string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ticketId) || ticketId.Length > 40)
            return null;

        if (!await ActivateTenantAsync(tenantId, ct))
            return null;

        var secret = ResolvePortalSecret();
        if (secret is null)
            return null;

        var expected = ComputeToken(secret, tenantId, ticketId);
        var provided = (token ?? string.Empty).Trim().ToLowerInvariant();
        if (provided.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(provided)))
            return null;

        return await _db.Incidents
            .FirstOrDefaultAsync(i => i.TenantId == tenantId
                && i.ExternalTicketId == ticketId.ToUpperInvariant(), ct);
    }

    private async Task<string> GenerateTicketIdAsync(CancellationToken ct)
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var chars = RandomNumberGenerator.GetString(alphabet, 6);
            var id = $"TKT-{chars}";
            var taken = await _db.Incidents
                .AsNoTracking()
                .AnyAsync(i => i.ExternalTicketId == id, ct);
            if (!taken)
                return id;
        }
        return $"TKT-{Guid.NewGuid():N}"[..12].ToUpperInvariant();
    }

    private void PersistComment(Incident incident, Guid tenantId, string comment)
    {
        var conversationId = incident.ConversationId;
        if (conversationId is null)
        {
            var fallbackConversation = new Conversation
            {
                TenantId = tenantId,
                Channel = "ticketing",
                ChannelUserId = incident.ExternalTicketId!
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
}

public sealed record CreatePortalTicketRequest(
    string? Name,
    string? Email,
    string Title,
    string? Description);

public sealed record PortalTicketCreatedResponse(
    string TicketId,
    string TrackingToken,
    string Status,
    string StatusUrl);

public sealed record PortalTicketCommentRequest(string Text);

public sealed record PortalTicketMessage(string Role, string Content, DateTimeOffset CreatedAt);

public sealed record PortalTicketResponse(
    string TicketId,
    string Title,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt,
    IReadOnlyList<PortalTicketMessage> Messages);
