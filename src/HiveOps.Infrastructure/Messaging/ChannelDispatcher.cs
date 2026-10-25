using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Ticketing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HiveOps.Infrastructure.Messaging;

/// <summary>
/// Resolves the concrete channel adapter by logical channel name and forwards
/// outbound messages. Unknown channels are logged and treated as no-ops so
/// notifications never break incident processing. The "web" channel has no
/// push transport, so its messages are persisted into the reporter's
/// conversation — they surface in the incident thread the reporter can read.
/// </summary>
public sealed class ChannelDispatcher : IChannelDispatcher
{
    private readonly IMessagingChannel _whatsAppChannel;
    private readonly TicketingChannel _ticketingChannel;
    private readonly AppDbContext _db;
    private readonly ILogger<ChannelDispatcher> _logger;

    public ChannelDispatcher(
        IMessagingChannel whatsAppChannel,
        TicketingChannel ticketingChannel,
        AppDbContext db,
        ILogger<ChannelDispatcher> logger)
    {
        _whatsAppChannel = whatsAppChannel;
        _ticketingChannel = ticketingChannel;
        _db = db;
        _logger = logger;
    }

    public async Task SendTextAsync(Guid tenantId, string channel, string channelUserId, string text, CancellationToken ct = default)
    {
        if (string.Equals(channel, "web", StringComparison.OrdinalIgnoreCase))
        {
            await SendToWebConversationAsync(tenantId, channelUserId, text, ct);
            return;
        }

        var adapter = ResolveChannel(channel, tenantId);
        if (adapter is null)
            return;

        try
        {
            await adapter.SendMessageAsync(channelUserId, text, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Channel dispatch failed. TenantId={TenantId} Channel={Channel} ChannelUserId={ChannelUserId}",
                tenantId, channel, channelUserId);
        }
    }

    public async Task SendButtonsAsync(
        Guid tenantId,
        string channel,
        string channelUserId,
        string text,
        IReadOnlyList<InteractiveButtonOption> buttons,
        string? footer,
        CancellationToken ct = default)
    {
        var adapter = ResolveChannel(channel, tenantId);
        if (adapter is null)
            return;

        try
        {
            await adapter.SendButtonMessageAsync(channelUserId, text, buttons, footer, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Channel button dispatch failed. TenantId={TenantId} Channel={Channel} ChannelUserId={ChannelUserId}",
                tenantId, channel, channelUserId);
        }
    }

    public async Task SendListAsync(
        Guid tenantId,
        string channel,
        string channelUserId,
        string text,
        string buttonText,
        IReadOnlyList<InteractiveListSection> sections,
        string? footer,
        CancellationToken ct = default)
    {
        var adapter = ResolveChannel(channel, tenantId);
        if (adapter is null)
            return;

        try
        {
            await adapter.SendListMessageAsync(channelUserId, text, buttonText, sections, footer, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Channel list dispatch failed. TenantId={TenantId} Channel={Channel} ChannelUserId={ChannelUserId}",
                tenantId, channel, channelUserId);
        }
    }

    /// <summary>
    /// The web channel has no outbound transport: the notification is appended
    /// to the reporter's most recent web conversation so it is delivered to the
    /// incident thread instead of being dropped.
    /// </summary>
    private async Task SendToWebConversationAsync(Guid tenantId, string channelUserId, string text, CancellationToken ct)
    {
        try
        {
            var conversation = await _db.Conversations
                .Where(c => c.TenantId == tenantId
                    && c.Channel == "web"
                    && c.ChannelUserId == channelUserId)
                .OrderByDescending(c => c.LastActivityAt)
                .FirstOrDefaultAsync(ct);

            if (conversation is null)
            {
                _logger.LogDebug(
                    "No web conversation found for user {ChannelUserId} tenant {TenantId}; notification skipped.",
                    channelUserId, tenantId);
                return;
            }

            _db.ConversationMessages.Add(new ConversationMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                TenantId = tenantId,
                Role = MessageRole.Assistant,
                AgentName = "HiveOps",
                Content = text,
                CreatedAt = DateTimeOffset.UtcNow
            });
            conversation.LastActivityAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Web channel persistence failed. TenantId={TenantId} ChannelUserId={ChannelUserId}",
                tenantId, channelUserId);
        }
    }

    private IMessagingChannel? ResolveChannel(string? channel, Guid tenantId)
    {
        switch (channel?.Trim().ToLowerInvariant())
        {
            case "whatsapp":
                return _whatsAppChannel;
            case "ticketing":
                return _ticketingChannel;
            default:
                _logger.LogWarning(
                    "No channel adapter registered for channel '{Channel}'. Message dropped. TenantId={TenantId}",
                    channel,
                    tenantId);
                return null;
        }
    }
}
