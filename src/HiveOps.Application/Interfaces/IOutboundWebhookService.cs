namespace HiveOps.Application.Interfaces;

public interface IOutboundWebhookService
{
    /// <summary>
    /// Delivers an event payload to the tenant's configured outbound webhook URL.
    /// No-ops when the tenant has no webhook configured or the event is not subscribed.
    /// </summary>
    Task SendEventAsync(Guid tenantId, string eventType, object payload, CancellationToken ct = default);
}
