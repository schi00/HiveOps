using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HiveOps.Api.Authentication;
using HiveOps.Api.Utilities;
using HiveOps.Application.Models;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Application.Interfaces;

namespace HiveOps.Api.Controllers;

[ApiController]
[Route("api/inbox")]
[Authorize(Roles = AppRoles.Tenant + "," + AppRoles.Admin,
    AuthenticationSchemes = Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme
        + "," + Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme
        + "," + HiveOps.Api.Authentication.ApiKeyAuthScheme.SchemeName)]
public sealed class InboxController : ControllerBase
{
    private const string AssignedAgentKey = "assignedAgent";
    private const string AssignedAtKey = "assignedAt";
    private const string InternalNoteAgentName = "InternalNote";

    private readonly AppDbContext _db;
    private readonly TenantContext _tenantContext;
    private readonly IMessagingChannel _messagingChannel;
    private readonly IConversationStateManager _stateManager;
    private readonly IOutboundWebhookService _webhook;

    public InboxController(
        AppDbContext db,
        TenantContext tenantContext,
        IMessagingChannel messagingChannel,
        IConversationStateManager stateManager,
        IOutboundWebhookService webhook)
    {
        _db = db;
        _tenantContext = tenantContext;
        _messagingChannel = messagingChannel;
        _stateManager = stateManager;
        _webhook = webhook;
    }

    [HttpGet("conversations")]
    public async Task<ActionResult<IReadOnlyList<InboxConversationDto>>> ListConversations(
        [FromQuery] string? status = null,
        [FromQuery] string? assignedTo = null,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        take = Math.Clamp(take, 1, 200);

        var slaThresholdMinutes = await GetSlaThresholdMinutesAsync(ct);

        IQueryable<Conversation> query = _db.Conversations.AsNoTracking();
        if (TryParseConversationStatus(status, out var parsedStatus))
            query = query.Where(c => c.Status == parsedStatus);

        var rows = await query
            .OrderByDescending(c => c.LastActivityAt)
            .Take(take)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var results = new List<InboxConversationDto>(rows.Count);
        foreach (var c in rows)
        {
            var state = await _stateManager.GetStateAsync(c.TenantId, c.Id, ct);
            string? assignedAgent = null;
            state?.FlowData.TryGetValue(AssignedAgentKey, out assignedAgent);

            if (!string.IsNullOrWhiteSpace(assignedTo) &&
                !string.Equals(assignedTo.Trim(), assignedAgent, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isOverdue = c.Status == ConversationStatus.AwaitingHuman
                && (now - c.LastActivityAt).TotalMinutes > slaThresholdMinutes;

            results.Add(new InboxConversationDto(
                c.Id,
                c.ChannelUserId,
                c.Channel,
                c.Status,
                c.CreatedAt,
                c.LastActivityAt,
                c.FailedClassificationCount,
                assignedAgent,
                isOverdue));
        }

        return Ok(results);
    }

    [HttpGet("conversations/{conversationId:guid}")]
    public async Task<ActionResult<InboxConversationDetailDto>> GetConversation(
        Guid conversationId,
        [FromQuery] bool includeInternal = false,
        CancellationToken ct = default)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        var conversation = await _db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        if (conversation is null) return NotFound();

        IQueryable<ConversationMessage> messagesQuery = _db.ConversationMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId);

        if (!includeInternal)
        {
            messagesQuery = messagesQuery.Where(m =>
                !(m.Role == MessageRole.System && m.AgentName == InternalNoteAgentName));
        }

        var messages = await messagesQuery
            .OrderBy(m => m.CreatedAt)
            .Select(m => new InboxMessageDto(
                m.Id,
                m.Role,
                m.Content,
                m.AgentName,
                m.ExternalMessageId,
                m.CreatedAt,
                m.Role == MessageRole.System && m.AgentName == InternalNoteAgentName))
            .ToListAsync(ct);

        var state = await _stateManager.GetStateAsync(conversation.TenantId, conversation.Id, ct);
        string? assignedAgent = null;
        string? assignedAtRaw = null;
        state?.FlowData.TryGetValue(AssignedAgentKey, out assignedAgent);
        state?.FlowData.TryGetValue(AssignedAtKey, out assignedAtRaw);
        DateTimeOffset? assignedAt = null;
        if (DateTimeOffset.TryParse(assignedAtRaw, out var parsedAssignedAt))
            assignedAt = parsedAssignedAt;

        var slaThresholdMinutes = await GetSlaThresholdMinutesAsync(ct);
        var isOverdue = conversation.Status == ConversationStatus.AwaitingHuman
            && (DateTimeOffset.UtcNow - conversation.LastActivityAt).TotalMinutes > slaThresholdMinutes;

        return Ok(new InboxConversationDetailDto(
            conversation.Id,
            conversation.ChannelUserId,
            conversation.Channel,
            conversation.Status,
            conversation.CreatedAt,
            conversation.LastActivityAt,
            assignedAgent,
            assignedAt,
            isOverdue,
            messages));
    }

        [HttpGet("conversations/{conversationId:guid}/customer-profile")]
        public async Task<IActionResult> GetCustomerProfile(Guid conversationId, CancellationToken ct)
        {
            if (!_tenantContext.IsResolved) return Unauthorized();

            var conversation = await _db.Conversations
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == conversationId, ct);
            if (conversation is null) return NotFound();

            var state = await _stateManager.GetStateAsync(conversation.TenantId, conversation.Id, ct);
            var data = state?.FlowData ?? new Dictionary<string, string>();

            return Ok(new
            {
                conversationId,
                channelUserId = conversation.ChannelUserId,
                customerPhone = data.GetValueOrDefault("customer_phone"),
                messageCount = data.TryGetValue("customer_msg_count", out var mc) && int.TryParse(mc, out var n) ? n : 0,
                firstSeenAt = data.TryGetValue("customer_first_seen", out var fs) && DateTimeOffset.TryParse(fs, out var dt) ? dt : (DateTimeOffset?)null,
                notes = data.GetValueOrDefault("customer_notes")
            });
        }
        [HttpGet("sla-summary")]
        public async Task<IActionResult> GetSlaSummary(CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        var slaThresholdMinutes = await GetSlaThresholdMinutesAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var awaitingConversations = await _db.Conversations
            .AsNoTracking()
            .Where(c => c.Status == ConversationStatus.AwaitingHuman)
            .Select(c => c.LastActivityAt)
            .ToListAsync(ct);

        var overdueTimestamps = awaitingConversations
            .Where(last => (now - last).TotalMinutes > slaThresholdMinutes)
            .ToList();

        DateTimeOffset? oldestOverdueAt = overdueTimestamps.Count > 0
            ? overdueTimestamps.Min()
            : null;

        return Ok(new
        {
            awaitingHuman = awaitingConversations.Count,
            overdue = overdueTimestamps.Count,
            slaThresholdMinutes,
            oldestOverdueAt
        });
    }

    [HttpPost("conversations/{conversationId:guid}/assign")]
    public async Task<IActionResult> Assign(Guid conversationId, [FromBody] AssignConversationRequest? request, CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        var actor = GetAuthenticatedAgentName();
        if (string.IsNullOrWhiteSpace(actor))
            return Unauthorized("Authenticated agent identity is required.");

        var conversation = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null) return NotFound();

        var state = await _stateManager.GetStateAsync(conversation.TenantId, conversation.Id, ct)
            ?? new ConversationStateContext
            {
                ConversationId = conversation.Id,
                TenantId = conversation.TenantId
            };

        state.FlowData[AssignedAgentKey] = actor;
        state.FlowData[AssignedAtKey] = DateTimeOffset.UtcNow.ToString("O");
        await _stateManager.SetStateAsync(conversation.TenantId, conversation.Id, state, ct);

        return Ok(new { message = "Conversation assigned.", conversationId, agent = actor });
    }

    [HttpPost("conversations/{conversationId:guid}/unassign")]
    public async Task<IActionResult> Unassign(Guid conversationId, CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        var conversation = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null) return NotFound();

        var state = await _stateManager.GetStateAsync(conversation.TenantId, conversation.Id, ct)
            ?? new ConversationStateContext
            {
                ConversationId = conversation.Id,
                TenantId = conversation.TenantId
            };

        state.FlowData.Remove(AssignedAgentKey);
        state.FlowData.Remove(AssignedAtKey);
        await _stateManager.SetStateAsync(conversation.TenantId, conversation.Id, state, ct);

        return Ok(new { message = "Conversation unassigned.", conversationId });
    }

    [HttpPost("conversations/{conversationId:guid}/takeover")]
    public async Task<IActionResult> Takeover(Guid conversationId, CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        var actor = GetAuthenticatedAgentName();
        if (string.IsNullOrWhiteSpace(actor))
            return Unauthorized("Authenticated agent identity is required.");

        var conversation = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null) return NotFound();

        conversation.Status = ConversationStatus.AwaitingHuman;
        conversation.LastActivityAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new { message = "Conversation marked as AwaitingHuman.", conversationId });
    }

    [HttpPost("conversations/{conversationId:guid}/release")]
    public async Task<IActionResult> Release(Guid conversationId, CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        var conversation = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null) return NotFound();

        conversation.Status = ConversationStatus.Active;
        conversation.FailedClassificationCount = 0;
        conversation.LastActivityAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new { message = "Conversation returned to bot.", conversationId });
    }

    [HttpPost("conversations/{conversationId:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid conversationId, CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        var actor = GetAuthenticatedAgentName();
        if (string.IsNullOrWhiteSpace(actor))
            return Unauthorized("Authenticated agent identity is required.");

        var conversation = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null) return NotFound();

        if (conversation.Status == ConversationStatus.Resolved)
            return Ok(new { message = "Already resolved.", conversationId });

        conversation.Status = ConversationStatus.Resolved;
        conversation.LastActivityAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

            await _webhook.SendEventAsync(
                conversation.TenantId,
                "conversation.resolved",
                new { conversationId, resolvedBy = actor, at = DateTimeOffset.UtcNow },
                ct);

            return Ok(new { message = "Conversation resolved.", conversationId, resolvedBy = actor });
    }

    [HttpGet("analytics")]
    public async Task<IActionResult> GetAnalytics(CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        var now = DateTimeOffset.UtcNow;
        var cutoff7d = now.AddDays(-7);
        var cutoff30d = now.AddDays(-30);
        var cutoff24h = now.AddHours(-24);

        var slaThresholdMinutes = await GetSlaThresholdMinutesAsync(ct);

        var allConversations = await _db.Conversations
            .AsNoTracking()
            .Select(c => new
            {
                c.Status,
                c.Channel,
                c.CreatedAt,
                c.LastActivityAt,
                c.FailedClassificationCount
            })
            .ToListAsync(ct);

        var byStatus = allConversations
            .GroupBy(c => c.Status)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        var byChannel = allConversations
            .GroupBy(c => c.Channel)
            .Select(g => new InboxChannelCountDto(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        int total = allConversations.Count;
        int escalated = allConversations.Count(c => c.FailedClassificationCount > 0);
        double escalationRate = total > 0 ? Math.Round((double)escalated / total * 100, 1) : 0;

        int awaitingHuman = allConversations.Count(c => c.Status == ConversationStatus.AwaitingHuman);
        int overdue = allConversations.Count(c =>
            c.Status == ConversationStatus.AwaitingHuman &&
            (now - c.LastActivityAt).TotalMinutes > slaThresholdMinutes);

        return Ok(new InboxAnalyticsDto(
            Total: total,
            ByStatus: byStatus,
            ByChannel: byChannel,
            CreatedLast24h: allConversations.Count(c => c.CreatedAt >= cutoff24h),
            CreatedLast7d: allConversations.Count(c => c.CreatedAt >= cutoff7d),
            CreatedLast30d: allConversations.Count(c => c.CreatedAt >= cutoff30d),
            ResolvedLast7d: allConversations.Count(c => c.Status == ConversationStatus.Resolved && c.LastActivityAt >= cutoff7d),
            ResolvedLast30d: allConversations.Count(c => c.Status == ConversationStatus.Resolved && c.LastActivityAt >= cutoff30d),
            EscalationRatePct: escalationRate,
            AwaitingHumanCount: awaitingHuman,
            OverdueCount: overdue,
            SlaThresholdMinutes: slaThresholdMinutes
        ));
    }

    [HttpPost("conversations/{conversationId:guid}/messages")]
    public async Task<IActionResult> SendHumanMessage(
        Guid conversationId,
        [FromBody] SendHumanMessageRequest request,
        CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();
        if (request is null || string.IsNullOrWhiteSpace(request.Content))
            return BadRequest("Message content is required.");

        var actor = GetAuthenticatedAgentName();
        if (string.IsNullOrWhiteSpace(actor))
            return Unauthorized("Authenticated agent identity is required.");

        var conversation = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null) return NotFound();

        var content = request.Content.Trim();
        var sender = actor;

        _db.ConversationMessages.Add(new ConversationMessage
        {
            ConversationId = conversation.Id,
            TenantId = conversation.TenantId,
            Role = MessageRole.Assistant,
            Content = content,
            AgentName = sender
        });

        conversation.LastActivityAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _messagingChannel.SendMessageAsync(conversation.ChannelUserId, content, ct);

        return Ok(new { message = "Human message sent.", conversationId });
    }

    [HttpPost("conversations/{conversationId:guid}/notes")]
    public async Task<IActionResult> AddInternalNote(
        Guid conversationId,
        [FromBody] AddInternalNoteRequest request,
        CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();
        if (request is null || string.IsNullOrWhiteSpace(request.Content))
            return BadRequest("Note content is required.");

        var actor = GetAuthenticatedAgentName();
        if (string.IsNullOrWhiteSpace(actor))
            return Unauthorized("Authenticated agent identity is required.");

        var conversation = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null) return NotFound();

        var content = request.Content.Trim();
        if (content.Length > 2000)
            return BadRequest("Note too long.");

        var noteContent = $"[{actor}] {content}";

        _db.ConversationMessages.Add(new ConversationMessage
        {
            ConversationId = conversation.Id,
            TenantId = conversation.TenantId,
            Role = MessageRole.System,
            Content = noteContent,
            AgentName = InternalNoteAgentName
        });

        conversation.LastActivityAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new { message = "Internal note saved.", conversationId });
    }

    private async Task<int> GetSlaThresholdMinutesAsync(CancellationToken ct)
    {
        var tenant = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == _tenantContext.TenantId, ct);
        return TenantSettingsJson.Parse(tenant?.ConfigJson).BotBehavior.HumanSlaThresholdMinutes;
    }

    private static bool TryParseConversationStatus(string? status, out ConversationStatus parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(status)) return false;
        return Enum.TryParse(status.Trim(), ignoreCase: true, out parsed);
    }

    private string? GetAuthenticatedAgentName()
    {
        if (User?.Identity?.IsAuthenticated != true)
            return null;

        // ApiKey-authenticated identities are not human agents
        if (User.Identity?.AuthenticationType == HiveOps.Api.Authentication.ApiKeyAuthScheme.SchemeName)
            return null;

        var username = User.FindFirstValue(AppClaimTypes.Username);
        if (!string.IsNullOrWhiteSpace(username))
            return username.Trim();

        return User.Identity?.Name;
    }
}

public sealed record SendHumanMessageRequest(string Content, string? Sender = null);
public sealed record AssignConversationRequest(string? Agent);
public sealed record AddInternalNoteRequest(string Content);

public sealed record InboxAnalyticsDto(
    int Total,
    Dictionary<string, int> ByStatus,
    IReadOnlyList<InboxChannelCountDto> ByChannel,
    int CreatedLast24h,
    int CreatedLast7d,
    int CreatedLast30d,
    int ResolvedLast7d,
    int ResolvedLast30d,
    double EscalationRatePct,
    int AwaitingHumanCount,
    int OverdueCount,
    int SlaThresholdMinutes);

public sealed record InboxChannelCountDto(string Channel, int Count);

public sealed record InboxConversationDto(
    Guid Id,
    string ChannelUserId,
    string Channel,
    ConversationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    int FailedClassificationCount,
    string? AssignedAgent,
    bool IsOverdue);

public sealed record InboxMessageDto(
    Guid Id,
    MessageRole Role,
    string Content,
    string? AgentName,
    string? ExternalMessageId,
    DateTimeOffset CreatedAt,
    bool IsInternal);

public sealed record InboxConversationDetailDto(
    Guid Id,
    string ChannelUserId,
    string Channel,
    ConversationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    string? AssignedAgent,
    DateTimeOffset? AssignedAt,
    bool IsOverdue,
    IReadOnlyList<InboxMessageDto> Messages);
