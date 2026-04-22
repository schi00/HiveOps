using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaaSBot.Api.Services;
using SaaSBot.Domain.Enums;
using SaaSBot.Domain.Interfaces;
using SaaSBot.Infrastructure.Multitenancy;
using SaaSBot.Infrastructure.Persistence;

namespace SaaSBot.Api.Controllers;

/// <summary>Allows merchant agents to take over and resolve escalated conversations.</summary>
[ApiController]
[Route("api/handoff")]
public sealed class HandoffController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConversationStateManager _stateManager;
    private readonly TenantContext _tenantContext;
    private readonly SupervisionNotificationService _notifier;

    public HandoffController(
        AppDbContext db,
        IConversationStateManager stateManager,
        TenantContext tenantContext,
        SupervisionNotificationService notifier)
    {
        _db = db;
        _stateManager = stateManager;
        _tenantContext = tenantContext;
        _notifier = notifier;
    }

    /// <summary>Returns a list of conversations currently awaiting human assistance.</summary>
    [HttpGet("pending")]
    public async Task<IActionResult> GetPending(CancellationToken ct)
    {
        var pending = await _db.Conversations
            .AsNoTracking()
            .Where(c => c.Status == ConversationStatus.AwaitingHuman)
            .OrderByDescending(c => c.LastActivityAt)
            .Select(c => new
            {
                c.Id,
                c.ChannelUserId,
                c.Channel,
                c.LastActivityAt
            })
            .ToListAsync(ct);

        return Ok(pending);
    }

    /// <summary>Marks a conversation as resolved and returns it to the bot.</summary>
    [HttpPost("{conversationId}/resolve")]
    public async Task<IActionResult> Resolve(Guid conversationId, CancellationToken ct)
    {
        var tenantId = _tenantContext.TenantId;

        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        if (conversation is null)
            return NotFound();

        conversation.Status = ConversationStatus.Active;
        await _db.SaveChangesAsync(ct);

        var state = await _stateManager.GetStateAsync(tenantId, conversationId, ct);
        if (state is not null)
        {
            state.State = Domain.Enums.ConversationState.Idle;
            state.FailedClassificationCount = 0;
            await _stateManager.SetStateAsync(tenantId, conversationId, state, ct);
        }

        return Ok(new { message = "Conversation returned to bot.", conversationId });
    }
}
