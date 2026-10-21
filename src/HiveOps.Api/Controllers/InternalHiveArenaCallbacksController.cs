using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using HiveOps.Application.Configuration;
using HiveOps.Application.HiveArena;
using HiveOps.Application.Services;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Api.Controllers;

/// <summary>
/// Internal HTTP ingress for HiveArena validation completion (until durable inbox exists).
/// </summary>
[ApiController]
[Route("api/internal/hivearena")]
[AllowAnonymous]
public sealed class InternalHiveArenaCallbacksController : ControllerBase
{
    /// <summary>After this many failed arena validations the incident escalates to an engineer.</summary>
    private const int MaxArenaDefeatsBeforeEscalation = 3;

    private readonly IIncidentMessageHistoryService _messageHistory;
    private readonly IOptionsMonitor<HiveOpsHiveArenaInboundOptions> _options;
    private readonly ILogger<InternalHiveArenaCallbacksController> _logger;
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenantContext;

    public InternalHiveArenaCallbacksController(
        IIncidentMessageHistoryService messageHistory,
        IOptionsMonitor<HiveOpsHiveArenaInboundOptions> options,
        ILogger<InternalHiveArenaCallbacksController> logger,
        AppDbContext db,
        ITenantContext tenantContext)
    {
        _messageHistory = messageHistory;
        _options = options;
        _logger = logger;
        _db = db;
        _tenantContext = tenantContext;
    }

    [HttpPost("validation-completed")]
    public async Task<IActionResult> ValidationCompleted(
        [FromHeader(Name = "X-HiveArena-Key")] string? key,
        [FromBody] HiveArenaValidationCompletedMessage body,
        CancellationToken cancellationToken)
    {
        var expected = _options.CurrentValue.SharedSecret;
        if (string.IsNullOrEmpty(expected) || key != expected)
        {
            _logger.LogWarning("Rejected HiveArena callback: missing or invalid X-HiveArena-Key.");
            return Unauthorized(new { error = "Invalid key" });
        }

        var outcomeName = body.Outcome switch
        {
            0 => "Survival",
            1 => "Defeat",
            2 => "Error",
            _ => body.Outcome.ToString()
        };

        var content =
            $"**HiveArena validation** outcome={outcomeName} request={body.RequestId}\n\n" +
            body.Summary +
            "\n\n--- llmReadableLog ---\n" +
            body.LlmReadableLog;

        await _messageHistory.AppendMessageToIncidentAsync(
            body.IncidentId,
            MessageRole.Assistant,
            content,
            agentName: "HiveArena",
            cancellationToken);

        _logger.LogInformation(
            "Recorded HiveArena validation outcome {Outcome} for incident {IncidentId}",
            outcomeName,
            body.IncidentId);

        var action = body.Outcome == 0
            ? "validated"
            : await HandleFailedValidationAsync(body.IncidentId, cancellationToken);

        return Ok(new { received = true, action });
    }

    /// <summary>
    /// Defeat/Error: reopens the incident for the auto-workflow (the arena log is now
    /// in the message history and feeds the next fix attempt). After
    /// <see cref="MaxArenaDefeatsBeforeEscalation"/> failed validations the incident
    /// is escalated to an engineer instead of looping forever.
    /// </summary>
    private async Task<string> HandleFailedValidationAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        var incident = await _db.Incidents
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);
        if (incident is null)
            return "incident-missing";

        if (_tenantContext is TenantContext tc && !tc.IsResolved)
            tc.SetTenant(incident.TenantId);

        var failures = (await _messageHistory.GetIncidentHistoryAsync(incidentId, cancellationToken))
            .Count(m => m.AgentName == "HiveArena"
                     && (m.Content.Contains("outcome=Defeat") || m.Content.Contains("outcome=Error")));

        if (failures >= MaxArenaDefeatsBeforeEscalation)
        {
            incident.AssignedTo = "engineer";
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(
                "Incident {IncidentId} escalated to engineer after {Failures} failed HiveArena validations.",
                incidentId, failures);
            return "escalated";
        }

        incident.Status = IncidentStatus.Open;
        incident.AssignedTo = "bot";
        incident.RequiresHumanApproval = false;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Incident {IncidentId} reopened for rework after HiveArena failure ({Failures}/{Max}).",
            incidentId, failures, MaxArenaDefeatsBeforeEscalation);
        return "reopened";
    }
}
