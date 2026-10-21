using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HiveOps.Infrastructure.Messaging;

/// <summary>
/// Sends proactive incident notifications back to the channel where the
/// incident was reported. All failures degrade to log + no-op so notification
/// problems never break incident processing.
/// </summary>
public sealed class IncidentChannelNotifier : IIncidentChannelNotifier
{
    private readonly AppDbContext _db;
    private readonly IChannelDispatcher _dispatcher;
    private readonly ILogger<IncidentChannelNotifier> _logger;

    public IncidentChannelNotifier(
        AppDbContext db,
        IChannelDispatcher dispatcher,
        ILogger<IncidentChannelNotifier> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task NotifyIncidentProgressAsync(Guid tenantId, Guid incidentId, string message, CancellationToken ct = default)
    {
        try
        {
            var target = await ResolveTargetAsync(tenantId, incidentId, ct);
            if (target is null)
                return;

            var (channel, channelUserId) = target.Value;
            await _dispatcher.SendTextAsync(tenantId, channel, channelUserId, message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Incident progress notification failed. TenantId={TenantId} IncidentId={IncidentId}",
                tenantId, incidentId);
        }
    }

    public async Task NotifyResolutionConfirmationRequestAsync(Guid tenantId, Guid incidentId, string resolutionSummary, CancellationToken ct = default)
    {
        try
        {
            var target = await ResolveTargetAsync(tenantId, incidentId, ct);
            if (target is null)
                return;

            var (channel, channelUserId) = target.Value;

            if (string.Equals(channel, "whatsapp", StringComparison.OrdinalIgnoreCase))
            {
                var buttons = new List<InteractiveButtonOption>
                {
                    new($"btn_confirm_resolved:{incidentId}", "Ya funciona"),
                    new($"btn_reopen_incident:{incidentId}", "Sigue fallando")
                };

                await _dispatcher.SendButtonsAsync(
                    tenantId,
                    channel,
                    channelUserId,
                    resolutionSummary,
                    buttons,
                    footer: null,
                    ct);
            }
            else
            {
                // Channels without interactive support get a plain-text confirmation request.
                var text = $"{resolutionSummary}\n\nRespondé CONFIRMAR si el problema está resuelto o REABRIR si sigue fallando.";
                await _dispatcher.SendTextAsync(tenantId, channel, channelUserId, text, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Resolution confirmation notification failed. TenantId={TenantId} IncidentId={IncidentId}",
                tenantId, incidentId);
        }
    }

    private async Task<(string Channel, string ChannelUserId)?> ResolveTargetAsync(Guid tenantId, Guid incidentId, CancellationToken ct)
    {
        // Ignore query filters and scope explicitly: background workers may run
        // without a resolved TenantContext on this DbContext instance.
        var incident = await _db.Incidents
            .IgnoreQueryFilters()
            .Include(i => i.Conversation)
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == incidentId && i.TenantId == tenantId, ct);

        if (incident is null)
        {
            _logger.LogWarning(
                "Incident {IncidentId} not found for tenant {TenantId}; notification skipped.",
                incidentId, tenantId);
            return null;
        }

        var conversation = incident.Conversation;
        if (conversation is null || string.IsNullOrWhiteSpace(conversation.Channel) || string.IsNullOrWhiteSpace(conversation.ChannelUserId))
        {
            _logger.LogWarning(
                "Incident {IncidentId} has no usable conversation/channel target; notification skipped.",
                incidentId);
            return null;
        }

        return (conversation.Channel, conversation.ChannelUserId);
    }
}
