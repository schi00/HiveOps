namespace HiveOps.Application.Interfaces;

public interface ISupervisionNotifier
{
    Task NotifyHandoffAsync(Guid tenantId, Guid conversationId, string reason, CancellationToken ct = default);
    Task NotifyFrustrationAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default);
}
