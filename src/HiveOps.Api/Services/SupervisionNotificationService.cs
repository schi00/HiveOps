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
    private readonly ChatOpsApprovalTokenService _tokenService;

    public SupervisionNotificationService(
        IHubContext<SupervisionHub> hubContext,
        IOutboundWebhookService webhook,
        ChatOpsApprovalTokenService? tokenService = null)
    {
        _hubContext = hubContext;
        _webhook = webhook;
        _tokenService = tokenService ?? new ChatOpsApprovalTokenService();
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
        var approveToken = _tokenService.CreateToken(tenantId, incidentId, "approve");
        var rejectToken = _tokenService.CreateToken(tenantId, incidentId, "reject");
        var approvePath = $"/api/support/chatops/incidents/{incidentId}/decision?tenantId={tenantId}&action=approve&token={approveToken}";
        var rejectPath = $"/api/support/chatops/incidents/{incidentId}/decision?tenantId={tenantId}&action=reject&token={rejectToken}";
        var slackBlocks = BuildSlackApprovalBlocks(tenantId, incidentId, branchName, approvePath, rejectPath, approveToken, rejectToken);
        var teamsCard = BuildTeamsApprovalCard(tenantId, incidentId, branchName, approvePath, rejectPath);

        var payload = new
        {
            incidentId,
            branchName,
            approvePath,
            rejectPath,
            slackBlocks,
            teamsCard,
            at = DateTimeOffset.UtcNow
        };
        var hub = _hubContext.Clients
            .Group($"tenant:{tenantId}")
            .SendAsync("ApprovalRequired", payload, ct);
        var wh = _webhook.SendEventAsync(tenantId, "approval.required", payload, ct);
        return Task.WhenAll(hub, wh);
    }

    public static object[] BuildSlackApprovalBlocks(
        Guid tenantId,
        Guid incidentId,
        string branchName,
        string approveUrl,
        string rejectUrl,
        string approveToken,
        string rejectToken) =>
    [
        new
        {
            type = "header",
            text = new { type = "plain_text", text = "HiveOps — Aprobación de Fix Requerida" }
        },
        new
        {
            type = "section",
            text = new
            {
                type = "mrkdwn",
                text = $"*Incidente:* `{incidentId}`\n*Rama / Fix:* `{branchName}`\nAprobá o rechazá el cambio en 1 clic:"
            }
        },
        new
        {
            type = "actions",
            elements = new object[]
            {
                new
                {
                    type = "button",
                    style = "primary",
                    text = new { type = "plain_text", text = "Aprobar Fix (1-Click)" },
                    url = approveUrl,
                    action_id = "chatops_approve",
                    value = $"{tenantId}|{incidentId}|approve|{approveToken}"
                },
                new
                {
                    type = "button",
                    style = "danger",
                    text = new { type = "plain_text", text = "Rechazar Fix" },
                    url = rejectUrl,
                    action_id = "chatops_reject",
                    value = $"{tenantId}|{incidentId}|reject|{rejectToken}"
                }
            }
        }
    ];

    public static object BuildTeamsApprovalCard(
        Guid tenantId,
        Guid incidentId,
        string branchName,
        string approveUrl,
        string rejectUrl) =>
        new
        {
            type = "message",
            attachments = new object[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.4",
                        body = new object[]
                        {
                            new
                            {
                                type = "TextBlock",
                                size = "Medium",
                                weight = "Bolder",
                                text = "HiveOps — Aprobación de Fix Requerida"
                            },
                            new
                            {
                                type = "FactSet",
                                facts = new object[]
                                {
                                    new { title = "Incident ID", value = incidentId.ToString() },
                                    new { title = "Tenant ID", value = tenantId.ToString() },
                                    new { title = "Branch / Fix", value = branchName }
                                }
                            }
                        },
                        actions = new object[]
                        {
                            new { type = "Action.OpenUrl", title = "Aprobar Fix (1-Click)", url = approveUrl },
                            new { type = "Action.OpenUrl", title = "Rechazar Fix", url = rejectUrl }
                        }
                    }
                }
            }
        };

    public Task NotifyLlmRetryUpdatedAsync(Guid tenantId, Guid incidentId, int attempt, CancellationToken ct = default)
    {
        var payload = new { incidentId, attempt, at = DateTimeOffset.UtcNow };
        return _hubContext.Clients
            .Group($"tenant:{tenantId}")
            .SendAsync("LlmRetryUpdated", payload, ct);
    }
}
