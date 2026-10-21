namespace HiveOps.Domain.Interfaces;

/// <summary>
/// Sends proactive, user-facing incident notifications back to the channel
/// where the incident was originally reported (WhatsApp, Ticketera, etc.).
/// </summary>
public interface IIncidentChannelNotifier
{
    Task NotifyIncidentProgressAsync(Guid tenantId, Guid incidentId, string message, CancellationToken ct = default);
    Task NotifyResolutionConfirmationRequestAsync(Guid tenantId, Guid incidentId, string resolutionSummary, CancellationToken ct = default);
}
