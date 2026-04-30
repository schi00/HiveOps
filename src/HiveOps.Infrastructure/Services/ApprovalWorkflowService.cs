using HiveOps.Application.Interfaces;
using HiveOps.Application.Services;
using HiveOps.Application.Support;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HiveOps.Infrastructure.Services;

/// <summary>
/// Service to manage human approval workflow for destructive changes.
/// </summary>
public interface IApprovalWorkflowService
{
    Task RequestApprovalAsync(
        Guid incidentId,
        string changeDescription,
        CancellationToken cancellationToken = default);

    Task<bool> ApproveChangeAsync(
        Guid incidentId,
        string approvalText,
        CancellationToken cancellationToken = default);

    Task RejectChangeAsync(
        Guid incidentId,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of approval workflow service.
/// </summary>
public sealed class ApprovalWorkflowService : IApprovalWorkflowService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGitService _gitService;
    private readonly IDeploymentService _deploymentService;
    private readonly IIncidentMessageHistoryService _messageHistory;

    public ApprovalWorkflowService(
        IServiceScopeFactory scopeFactory,
        IGitService gitService,
        IDeploymentService deploymentService,
        IIncidentMessageHistoryService messageHistory)
    {
        _scopeFactory = scopeFactory;
        _gitService = gitService;
        _deploymentService = deploymentService;
        _messageHistory = messageHistory;
    }

    /// <summary>
    /// Requests human approval for a change.
    /// </summary>
    public async Task RequestApprovalAsync(
        Guid incidentId,
        string changeDescription,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var incident = await db.Incidents.FindAsync(incidentId, cancellationToken);
        if (incident is null)
            throw new InvalidOperationException($"Incident {incidentId} not found");

        // Update incident status to indicate approval is required
        incident.RequiresHumanApproval = true;
        incident.Status = IncidentStatus.AwaitingApproval;
        incident.UpdatedAt = DateTimeOffset.UtcNow;

        await _messageHistory.AppendMessageToIncidentAsync(
            incidentId,
            MessageRole.Assistant,
            $"Solicitando aprobación humana para: {changeDescription}",
            "ApprovalWorkflow",
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        // Send notification
        var notifier = scope.ServiceProvider.GetRequiredService<ISupervisionNotifier>();
        await notifier.NotifyApprovalRequiredAsync(incident.TenantId, incidentId, changeDescription, cancellationToken);
    }

    /// <summary>
    /// Approves a change and proceeds with deployment.
    /// </summary>
    public async Task<bool> ApproveChangeAsync(
        Guid incidentId,
        string approvalText,
        CancellationToken cancellationToken = default)
    {
        var explicitApproval = approvalText.Contains("confirmo", StringComparison.OrdinalIgnoreCase)
            || approvalText.Contains("approve", StringComparison.OrdinalIgnoreCase)
            || approvalText.Contains("si", StringComparison.OrdinalIgnoreCase)
            || approvalText.Contains("yes", StringComparison.OrdinalIgnoreCase);

        if (!explicitApproval)
            return false;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var incident = await db.Incidents
            .Include(i => i.Tenant)
            .FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);

        if (incident is null)
            throw new InvalidOperationException($"Incident {incidentId} not found");

        await _messageHistory.AppendMessageToIncidentAsync(
            incidentId,
            MessageRole.Assistant,
            "Aprobación recibida. Procediendo con deployment...",
            "ApprovalWorkflow",
            cancellationToken);

        // Execute based on incident category
        if (incident.Category == IncidentCategory.Code && !string.IsNullOrEmpty(incident.GitBranch))
        {
            var cfg = scope.ServiceProvider.GetRequiredService<ITenantConfigService>();
            if (!await TenantDeployPolicy.IsManualDeployAllowedAsync(cfg, incident.TenantId, cancellationToken))
            {
                await _messageHistory.AppendMessageToIncidentAsync(
                    incidentId,
                    MessageRole.Assistant,
                    "Despliegue manual deshabilitado para este tenant (configuración DeployGit).",
                    "ApprovalWorkflow",
                    cancellationToken);
                incident.Status = IncidentStatus.InProgress;
                incident.RequiresHumanApproval = false;
                incident.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }

            // Deploy code fix
            if (!string.IsNullOrEmpty(incident.GitCommitHash))
            {
                var deployId = await _deploymentService.TriggerDeployAsync(incident.GitBranch, incident.GitCommitHash, cancellationToken);
                incident.DeployStatus = deployId;
            }
            else
            {
                // Push branch first
                await _gitService.PushAsync(incident.TenantId, incident.GitBranch, cancellationToken);
                var deployId = await _deploymentService.TriggerDeployAsync(incident.GitBranch, "", cancellationToken);
                incident.DeployStatus = deployId;
            }
        }
        else if (incident.Category == IncidentCategory.Database)
        {
            // Execute DB fix (already validated and backed up)
            // The SQL would be in ResolutionNotes or attached as an IncidentAttachment
            // For now, mark as resolved since the fix was already applied during proposal
        }

        // Update incident status
        incident.Status = IncidentStatus.InProgress;
        incident.RequiresHumanApproval = false;
        incident.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        await _messageHistory.AppendMessageToIncidentAsync(
            incidentId,
            MessageRole.Assistant,
            "Cambio aprobado y deployment iniciado",
            "ApprovalWorkflow",
            cancellationToken);

        return true;
    }

    /// <summary>
    /// Rejects a change and discards it.
    /// </summary>
    public async Task RejectChangeAsync(
        Guid incidentId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var incident = await db.Incidents.FindAsync(incidentId, cancellationToken);
        if (incident is null)
            throw new InvalidOperationException($"Incident {incidentId} not found");

        await _messageHistory.AppendMessageToIncidentAsync(
            incidentId,
            MessageRole.Assistant,
            $"Cambio rechazado: {reason}",
            "ApprovalWorkflow",
            cancellationToken);

        // Discard the proposed change
        if (!string.IsNullOrEmpty(incident.GitBranch))
        {
            // Note: GitService doesn't have a delete branch method, so we just log this
            // In a real implementation, you might want to add that capability
            // Notify via supervision notifier that approval was rejected
            var notifier = scope.ServiceProvider.GetRequiredService<ISupervisionNotifier>();
            await notifier.NotifyIncidentCreatedAsync(incident.TenantId, incident.Id, $"Change rejected: {reason}", incident.Severity.ToString(), cancellationToken);
        }

        // Update incident status
        incident.Status = IncidentStatus.Open; // Reset to Open for manual handling
        incident.RequiresHumanApproval = false;
        incident.ResolutionNotes = $"Change rejected: {reason}";
        incident.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }
}
