namespace HiveOps.Application.Interfaces;

public interface ISupervisionNotifier
{
    Task NotifyHandoffAsync(Guid tenantId, Guid conversationId, string reason, CancellationToken ct = default);
    Task NotifyFrustrationAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default);
    Task NotifyIncidentCreatedAsync(Guid tenantId, Guid incidentId, string title, string severity, CancellationToken ct = default);
    Task NotifyApprovalRequiredAsync(Guid tenantId, Guid incidentId, string branchName, CancellationToken ct = default);
}
