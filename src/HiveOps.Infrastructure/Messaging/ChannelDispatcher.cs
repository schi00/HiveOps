using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Ticketing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HiveOps.Infrastructure.Messaging;

/// <summary>
/// Resolves the concrete channel adapter by logical channel name and forwards
/// outbound messages. Unknown channels are logged and treated as no-ops so
/// notifications never break incident processing. "web" and the hosted
/// "ticketing" channel have no push transport — their messages are persisted
/// into the reporter's conversation so they surface in the incident thread.
/// An external ticketing system still wins when Ticketing:BaseUrl is set.
/// </summary>
public sealed class ChannelDispatcher : IChannelDispatcher
{
    private readonly IMessagingChannel _whatsAppChannel;
    private readonly TicketingChannel _ticketingChannel;
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChannelDispatcher> _logger;

    public ChannelDispatcher(
        IMessagingChannel whatsAppChannel,
        TicketingChannel ticketingChannel,
        AppDbContext db,
        IConfiguration configuration,
        ILogger<ChannelDispatcher> logger)
    {
        _whatsAppChannel = whatsAppChannel;
        _ticketingChannel = ticketingChannel;
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendTextAsync(Guid tenantId, string channel, string channelUserId, string text, CancellationToken ct = default)
    {
        if (IsHostedChannel(channel))
        {
            await PersistToConversationAsync(tenantId, channel, channelUserId, text, ct);
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
        if (IsHostedChannel(channel))
        {
            var combined = text
                + "\n\nOpciones:\n"
                + string.Join("\n", buttons.Where(b => !string.IsNullOrWhiteSpace(b.Title)).Select(b => $"- {b.Title.Trim()}"));
            await PersistToConversationAsync(tenantId, channel, channelUserId, combined.Trim(), ct);
            return;
        }

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
        if (IsHostedChannel(channel))
        {
            await PersistToConversationAsync(tenantId, channel, channelUserId, text, ct);
            return;
        }

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
    /// Hosted channels (web chat, hosted ticket portal) have no outbound
    /// transport: the notification is appended to the reporter's most recent
    /// conversation on that channel so it lands in the thread they can read.
    /// </summary>
    private bool IsHostedChannel(string? channel)
    {
        if (string.Equals(channel, "web", StringComparison.OrdinalIgnoreCase))
            return true;

        // Hosted ticketing portal is default; an external ticket system wins
        // when Ticketing:BaseUrl is configured.
        if (string.Equals(channel, "ticketing", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(_configuration["Ticketing:BaseUrl"]);

        return false;
    }

    private async Task PersistToConversationAsync(
        Guid tenantId, string channel, string channelUserId, string text, CancellationToken ct)
    {
        try
        {
            var conversation = await _db.Conversations
                .Where(c => c.TenantId == tenantId
                    && c.Channel == channel
                    && c.ChannelUserId == channelUserId)
                .OrderByDescending(c => c.LastActivityAt)
                .FirstOrDefaultAsync(ct);

            if (conversation is null)
            {
                _logger.LogDebug(
                    "No {Channel} conversation found for user {ChannelUserId} tenant {TenantId}; notification skipped.",
                    channel, channelUserId, tenantId);
                return;
            }

            // Link the message to the incident on this conversation so the
            // ticket portal thread (filtered by IncidentId) shows it.
            var incidentId = await _db.Incidents
                .Where(i => i.TenantId == tenantId && i.ConversationId == conversation.Id)
                .OrderByDescending(i => i.UpdatedAt)
                .Select(i => (Guid?)i.Id)
                .FirstOrDefaultAsync(ct);

            _db.ConversationMessages.Add(new ConversationMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                TenantId = tenantId,
                IncidentId = incidentId,
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
                "Hosted channel persistence failed. TenantId={TenantId} Channel={Channel} ChannelUserId={ChannelUserId}",
                tenantId, channel, channelUserId);
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
