using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Ticketing;
using Microsoft.Extensions.Logging;

namespace HiveOps.Infrastructure.Messaging;

/// <summary>
/// Resolves the concrete channel adapter by logical channel name and forwards
/// outbound messages. Unknown channels are logged and treated as no-ops so
/// notifications never break incident processing.
/// </summary>
public sealed class ChannelDispatcher : IChannelDispatcher
{
    private readonly IMessagingChannel _whatsAppChannel;
    private readonly TicketingChannel _ticketingChannel;
    private readonly ILogger<ChannelDispatcher> _logger;

    public ChannelDispatcher(
        IMessagingChannel whatsAppChannel,
        TicketingChannel ticketingChannel,
        ILogger<ChannelDispatcher> logger)
    {
        _whatsAppChannel = whatsAppChannel;
        _ticketingChannel = ticketingChannel;
        _logger = logger;
    }

    public async Task SendTextAsync(Guid tenantId, string channel, string channelUserId, string text, CancellationToken ct = default)
    {
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
