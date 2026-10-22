using System.Net;
using System.Text.Json;
using HiveOps.Agents.Support;
using HiveOps.Api.Services;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Services;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Services;
using HiveOps.Workers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HiveOps.Api.Controllers;

[ApiController]
[Route("api/support/chatops")]
public sealed class ChatOpsApprovalsController : ControllerBase
{
    private readonly ChatOpsApprovalTokenService _tokenService;
    private readonly ITenantContext _tenantContext;
    private readonly IApprovalWorkflowService _approvalWorkflow;
    private readonly AppDbContext _db;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ChatOpsApprovalsController> _logger;

    public ChatOpsApprovalsController(
        ChatOpsApprovalTokenService tokenService,
        ITenantContext tenantContext,
        IApprovalWorkflowService approvalWorkflow,
        AppDbContext db,
        IServiceProvider serviceProvider,
        ILogger<ChatOpsApprovalsController> logger)
    {
        _tokenService = tokenService;
        _tenantContext = tenantContext;
        _approvalWorkflow = approvalWorkflow;
        _db = db;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    [AllowAnonymous]
    [HttpGet("incidents/{incidentId:guid}/decision")]
    public async Task<IActionResult> DecideByLinkAsync(
        Guid incidentId,
        [FromQuery] Guid tenantId,
        [FromQuery] string action,
        [FromQuery] string token,
        [FromQuery] string? reason = null,
        CancellationToken ct = default)
    {
        var normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!_tokenService.TryValidateToken(tenantId, incidentId, normalizedAction, token, out var errorReason))
        {
            if (WantsJson())
                return Unauthorized(new { ok = false, error = errorReason ?? "Invalid or expired approval token." });

            return new ContentResult
            {
                StatusCode = StatusCodes.Status401Unauthorized,
                ContentType = "text/html; charset=utf-8",
                Content = BuildHtmlPage(
                    incidentId,
                    title: "Enlace Inválido o Expirado",
                    badgeText: "No Autorizado",
                    badgeColor: "#dc2626",
                    detail: errorReason ?? "El token de aprobación ChatOps no es válido o ha expirado.")
            };
        }

        var executionResult = await ExecuteDecisionAsync(tenantId, incidentId, normalizedAction, reason, ct);

        if (WantsJson())
        {
            return Ok(new
            {
                ok = true,
                incidentId,
                tenantId,
                status = normalizedAction,
                decisionLabel = normalizedAction == "approve" ? "Aprobado" : "Rechazado",
                hasSqlScript = executionResult.HasSqlScript,
                workflowExecuted = executionResult.WorkflowResult
            });
        }

        var isApprove = normalizedAction == "approve";
        var statusLabel = isApprove ? "Aprobado" : "Rechazado";
        var badgeColor = isApprove ? "#16a34a" : "#dc2626";
        var detailText = isApprove
            ? $"El fix para el incidente {incidentId} fue Aprobado vía ChatOps 1-Click ({(executionResult.HasSqlScript ? "Script SQL / Playbook" : "Playbook / Code Fix")})."
            : $"El fix para el incidente {incidentId} fue Rechazado vía ChatOps 1-Click. Motivo: {reason ?? "Rechazado vía ChatOps 1-click"}.";

        return new ContentResult
        {
            StatusCode = StatusCodes.Status200OK,
            ContentType = "text/html; charset=utf-8",
            Content = BuildHtmlPage(
                incidentId,
                title: $"Fix {statusLabel}",
                badgeText: statusLabel,
                badgeColor: badgeColor,
                detail: detailText)
        };
    }

    [AllowAnonymous]
    [HttpPost("slack-interactive")]
    public async Task<IActionResult> SlackInteractiveAsync(CancellationToken ct = default)
    {
        var parsed = await TryParseInteractiveRequestAsync(ct);
        if (parsed is null)
            return BadRequest(new { ok = false, error = "Invalid Slack interactive payload or JSON request body." });

        var normalizedAction = (parsed.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (!_tokenService.TryValidateToken(parsed.TenantId, parsed.IncidentId, normalizedAction, parsed.Token, out var errorReason))
            return Unauthorized(new { ok = false, error = errorReason ?? "Invalid or expired approval token." });

        await ExecuteDecisionAsync(parsed.TenantId, parsed.IncidentId, normalizedAction, parsed.Reason, ct);

        return Ok(new
        {
            ok = true,
            status = normalizedAction,
            incidentId = parsed.IncidentId,
            tenantId = parsed.TenantId
        });
    }

    [Authorize]
    [HttpGet("incidents/{incidentId:guid}/links")]
    public async Task<IActionResult> GetSignedApprovalLinksAsync(
        Guid incidentId,
        CancellationToken ct = default)
    {
        var incident = await _db.Incidents
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == incidentId, ct);

        var tenantId = _tenantContext.TenantId != Guid.Empty
            ? _tenantContext.TenantId
            : (incident?.TenantId ?? Guid.Empty);

        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "Unable to resolve TenantId for signed ChatOps links." });

        var approveToken = _tokenService.CreateToken(tenantId, incidentId, "approve");
        var rejectToken = _tokenService.CreateToken(tenantId, incidentId, "reject");

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var approvePath = $"/api/support/chatops/incidents/{incidentId}/decision?tenantId={tenantId}&action=approve&token={approveToken}";
        var rejectPath = $"/api/support/chatops/incidents/{incidentId}/decision?tenantId={tenantId}&action=reject&token={rejectToken}";
        var approveUrl = $"{baseUrl}{approvePath}";
        var rejectUrl = $"{baseUrl}{rejectPath}";

        var branchOrFix = incident?.Title
            ?? $"incident-{incidentId.ToString("N")[..8]}";

        var slackBlocks = SupervisionNotificationService.BuildSlackApprovalBlocks(
            tenantId, incidentId, branchOrFix, approveUrl, rejectUrl, approveToken, rejectToken);
        var teamsCard = SupervisionNotificationService.BuildTeamsApprovalCard(
            tenantId, incidentId, branchOrFix, approveUrl, rejectUrl);

        return Ok(new
        {
            incidentId,
            tenantId,
            approveUrl,
            rejectUrl,
            approvePath,
            rejectPath,
            slackPayload = new
            {
                text = $"[HiveOps] Aprobación requerida para incidente {incidentId}",
                blocks = slackBlocks
            },
            teamsCard
        });
    }

    private async Task<(bool HasSqlScript, bool WorkflowResult)> ExecuteDecisionAsync(
        Guid tenantId,
        Guid incidentId,
        string normalizedAction,
        string? reason,
        CancellationToken ct)
    {
        ResolveTenantContext(_tenantContext, tenantId);

        var incident = await _db.Incidents
            .Include(i => i.Attachments)
            .FirstOrDefaultAsync(i => i.Id == incidentId && i.TenantId == tenantId, ct);

        var hasSqlScript = incident?.Attachments?.Any(a => a.Type == IncidentAttachmentType.SqlScript) == true;
        bool workflowResult;

        var auditLedger = _serviceProvider.GetService<IIncidentAuditLedger>();

        if (normalizedAction == "approve")
        {
            workflowResult = await _approvalWorkflow.ApproveChangeAsync(
                incidentId,
                "confirmo via ChatOps 1-click",
                ct);

            if (auditLedger is not null)
            {
                await auditLedger.AppendAsync(
                    tenantId,
                    incidentId,
                    "Approval",
                    "ChatOps.OneClick",
                    JsonSerializer.Serialize(new
                    {
                        action = "approve",
                        channel = "ChatOps.OneClick",
                        hasSqlScript,
                        workflowResult
                    }),
                    ct);
            }
        }
        else
        {
            var effectiveReason = string.IsNullOrWhiteSpace(reason)
                ? "Rechazado vía ChatOps 1-click"
                : reason.Trim();

            await _approvalWorkflow.RejectChangeAsync(
                incidentId,
                effectiveReason,
                ct);
            workflowResult = true;

            if (auditLedger is not null)
            {
                await auditLedger.AppendAsync(
                    tenantId,
                    incidentId,
                    "Rejection",
                    "ChatOps.OneClick",
                    JsonSerializer.Serialize(new
                    {
                        action = "reject",
                        channel = "ChatOps.OneClick",
                        reason = effectiveReason,
                        workflowResult
                    }),
                    ct);
            }
        }

        _logger.LogInformation(
            "ChatOps 1-click decision '{Action}' executed for Incident {IncidentId} (Tenant {TenantId}, HasSql={HasSql})",
            normalizedAction, incidentId, tenantId, hasSqlScript);

        return (hasSqlScript, workflowResult);
    }

    private async Task<ChatOpsInteractiveDecisionRequest?> TryParseInteractiveRequestAsync(CancellationToken ct)
    {
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(ct);
            if (form.TryGetValue("payload", out var payloadValues) && !string.IsNullOrWhiteSpace(payloadValues.FirstOrDefault()))
            {
                using var doc = JsonDocument.Parse(payloadValues.First()!);
                if (doc.RootElement.TryGetProperty("actions", out var actions)
                    && actions.ValueKind == JsonValueKind.Array
                    && actions.GetArrayLength() > 0)
                {
                    var firstAction = actions[0];
                    if (firstAction.TryGetProperty("value", out var valProp) && valProp.ValueKind == JsonValueKind.String)
                    {
                        var parts = (valProp.GetString() ?? string.Empty).Split('|');
                        if (parts.Length >= 4
                            && Guid.TryParse(parts[0], out var tId)
                            && Guid.TryParse(parts[1], out var iId))
                        {
                            return new ChatOpsInteractiveDecisionRequest(
                                tId,
                                iId,
                                parts[2],
                                parts[3],
                                parts.Length > 4 ? parts[4] : null);
                        }
                    }
                }
            }
        }

        try
        {
            var body = await JsonSerializer.DeserializeAsync<ChatOpsInteractiveDecisionRequest>(
                Request.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                ct);
            if (body is not null && body.TenantId != Guid.Empty && body.IncidentId != Guid.Empty)
                return body;
        }
        catch
        {
            // Ignore malformed JSON body
        }

        return null;
    }

    private static void ResolveTenantContext(ITenantContext tenantContext, Guid tenantId)
    {
        var type = tenantContext.GetType();
        foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Guid))
            {
                method.Invoke(tenantContext, [tenantId]);
                return;
            }
            if (parameters.Length == 2 && parameters[0].ParameterType == typeof(Guid) && parameters[1].ParameterType == typeof(string))
            {
                method.Invoke(tenantContext, [tenantId, "chatops"]);
                return;
            }
        }

        var prop = type.GetProperty(nameof(ITenantContext.TenantId), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (prop?.CanWrite == true)
        {
            prop.SetValue(tenantContext, tenantId);
            return;
        }

        var field = type.GetField("<TenantId>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(tenantContext, tenantId);
    }

    private bool WantsJson()
    {
        var accept = Request.Headers.Accept.ToString();
        return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildHtmlPage(
        Guid incidentId,
        string title,
        string badgeText,
        string badgeColor,
        string detail)
    {
        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedBadge = WebUtility.HtmlEncode(badgeText);
        var encodedDetail = WebUtility.HtmlEncode(detail);
        var encodedIncident = WebUtility.HtmlEncode(incidentId.ToString());

        return $$"""
        <!DOCTYPE html>
        <html lang="es">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>HiveOps ChatOps — {{encodedTitle}}</title>
            <style>
                body { font-family: system-ui, -apple-system, sans-serif; background: #0f172a; color: #f8fafc; display: flex; align-items: center; justify-content: center; min-height: 100vh; margin: 0; padding: 1rem; }
                .card { background: #1e293b; border: 1px solid #334155; border-radius: 12px; max-width: 520px; width: 100%; padding: 2rem; box-shadow: 0 20px 25px -5px rgba(0,0,0,0.4); }
                .brand { display: inline-block; font-size: 0.75rem; font-weight: 700; letter-spacing: 0.08em; text-transform: uppercase; background: #3b82f6; color: #fff; padding: 0.25rem 0.6rem; border-radius: 999px; margin-bottom: 1rem; }
                .badge { display: inline-block; font-size: 0.875rem; font-weight: 700; background: {{badgeColor}}; color: #fff; padding: 0.35rem 0.75rem; border-radius: 6px; margin-bottom: 0.75rem; }
                h1 { font-size: 1.5rem; margin: 0 0 0.75rem 0; }
                p { color: #cbd5e1; line-height: 1.5; margin: 0 0 1rem 0; }
                .meta { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 0.8rem; color: #94a3b8; background: #0f172a; padding: 0.75rem; border-radius: 6px; }
            </style>
        </head>
        <body>
            <div class="card">
                <div class="brand">HiveOps ChatOps</div>
                <div><span class="badge">{{encodedBadge}}</span></div>
                <h1>{{encodedTitle}}</h1>
                <p>{{encodedDetail}}</p>
                <div class="meta">Incident ID: {{encodedIncident}}</div>
            </div>
        </body>
        </html>
        """;
    }

    public sealed record ChatOpsInteractiveDecisionRequest(
        Guid TenantId,
        Guid IncidentId,
        string Action,
        string Token,
        string? Reason = null);
}
