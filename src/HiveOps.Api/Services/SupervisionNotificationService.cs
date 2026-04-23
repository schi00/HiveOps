using Microsoft.AspNetCore.SignalR;
using HiveOps.Application.Interfaces;
using HiveOps.Api.Hubs;
using Microsoft.Extensions.Logging;

namespace HiveOps.Api.Services;

/// <summary>
/// Sends real-time notifications to the merchant dashboard when a conversation
/// needs human intervention. Injected into the Application layer via interface.
/// </summary>
public sealed class SupervisionNotificationService : ISupervisionNotifier
{
    private readonly IHubContext<SupervisionHub> _hubContext;
    private readonly IOutboundWebhookService _webhook;

    public SupervisionNotificationService(
        IHubContext<SupervisionHub> hubContext,
        IOutboundWebhookService webhook)
    {
        _hubContext = hubContext;
        _webhook = webhook;
    }

    public Task NotifyHandoffAsync(
        Guid tenantId, Guid conversationId, string reason, CancellationToken ct = default)
    {
        var payload = new { conversationId, reason, at = DateTimeOffset.UtcNow };
        var hub = _hubContext.Clients
            .Group($"tenant:{tenantId}")
            .SendAsync("HandoffRequested", payload, ct);
        var wh = _webhook.SendEventAsync(tenantId, "handoff.requested", payload, ct);
        return Task.WhenAll(hub, wh);
    }

    public Task NotifyFrustrationAsync(
        Guid tenantId, Guid conversationId, CancellationToken ct = default)
    {
        var payload = new { conversationId, at = DateTimeOffset.UtcNow };
        var hub = _hubContext.Clients
            .Group($"tenant:{tenantId}")
            .SendAsync("FrustrationDetected", payload, ct);
        var wh = _webhook.SendEventAsync(tenantId, "frustration.detected", payload, ct);
        return Task.WhenAll(hub, wh);
    }

    public Task NotifyIncidentCreatedAsync(
        Guid tenantId, Guid incidentId, string title, string severity, CancellationToken ct = default)
    {
        var payload = new { incidentId, title, severity, at = DateTimeOffset.UtcNow };
        var hub = _hubContext.Clients
            .Group($"tenant:{tenantId}")
            .SendAsync("IncidentCreated", payload, ct);
        var wh = _webhook.SendEventAsync(tenantId, "incident.created", payload, ct);
        return Task.WhenAll(hub, wh);
    }

    public Task NotifyApprovalRequiredAsync(
        Guid tenantId, Guid incidentId, string branchName, CancellationToken ct = default)
    {
        var payload = new { incidentId, branchName, at = DateTimeOffset.UtcNow };
        var hub = _hubContext.Clients
            .Group($"tenant:{tenantId}")
            .SendAsync("ApprovalRequired", payload, ct);
        var wh = _webhook.SendEventAsync(tenantId, "approval.required", payload, ct);
        return Task.WhenAll(hub, wh);
    }
}
