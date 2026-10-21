using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using HiveOps.Application.Configuration;
using HiveOps.Application.HiveArena;
using HiveOps.Application.Services;
using HiveOps.Domain.Entities;
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
    private readonly IDeploymentService _deploymentService;
    private readonly IGitService _gitService;
    private readonly IIncidentAuditLedger _auditLedger;

    public InternalHiveArenaCallbacksController(
        IIncidentMessageHistoryService messageHistory,
        IOptionsMonitor<HiveOpsHiveArenaInboundOptions> options,
        ILogger<InternalHiveArenaCallbacksController> logger,
        AppDbContext db,
        ITenantContext tenantContext,
        IDeploymentService deploymentService,
        IGitService gitService,
        IIncidentAuditLedger auditLedger)
    {
        _messageHistory = messageHistory;
        _options = options;
        _logger = logger;
        _db = db;
        _tenantContext = tenantContext;
        _deploymentService = deploymentService;
        _gitService = gitService;
        _auditLedger = auditLedger;
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
            ? await HandleSuccessfulValidationAsync(body, cancellationToken)
            : await HandleFailedValidationAsync(body, cancellationToken);

        return Ok(new { received = true, action });
    }

    /// <summary>
    /// Survival: marks the incident as arena-validated, pushes the fix branch,
    /// opens the pull request carrying the root-cause hypothesis, seals the
    /// audit chain with a "PullRequest" entry and triggers the deployment —
    /// the step the worker deferred while <c>ArenaValidationStatus = "Pending"</c>.
    /// Everything past the status update is best-effort so the callback never 500s.
    /// </summary>
    private async Task<string> HandleSuccessfulValidationAsync(
        HiveArenaValidationCompletedMessage body, CancellationToken cancellationToken)
    {
        var incident = await _db.Incidents
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == body.IncidentId, cancellationToken);
        if (incident is null)
            return "incident-missing";

        if (_tenantContext is TenantContext tc && !tc.IsResolved)
            tc.SetTenant(incident.TenantId);

        incident.ArenaValidationStatus = "Survived";
        incident.UpdatedAt = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(incident.GitBranch))
        {
            _logger.LogWarning(
                "Incident {IncidentId} survived HiveArena but has no git branch; skipping PR/deploy.",
                incident.Id);
            await _db.SaveChangesAsync(cancellationToken);
            return "validated";
        }

        // Push the fix branch so the provider can open the PR (best effort).
        try
        {
            await _gitService.PushAsync(incident.TenantId, incident.GitBranch, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Branch push failed after HiveArena survival for incident {IncidentId}.", incident.Id);
        }

        // Open the pull request with the hypothesis/evidence summary (best effort).
        string? prUrl = null;
        try
        {
            prUrl = await _deploymentService.CreatePullRequestAsync(
                incident.TenantId,
                incident.GitBranch,
                $"[HiveOps] Auto-fix {incident.Id}: {incident.Title}",
                BuildPullRequestBody(incident, body),
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(prUrl))
                incident.PullRequestUrl = prUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Pull request creation failed after HiveArena survival for incident {IncidentId}.", incident.Id);
        }

        try
        {
            await _auditLedger.AppendAsync(
                incident.TenantId,
                incident.Id,
                "PullRequest",
                "HiveOps.HiveArena",
                new
                {
                    url = prUrl,
                    branch = incident.GitBranch,
                    outcome = "Survival",
                    requestId = body.RequestId
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Audit append failed for incident {IncidentId}.", incident.Id);
        }

        // Trigger the deploy the worker deferred while the arena gate was pending.
        try
        {
            var deployId = await _deploymentService.TriggerDeployAsync(
                incident.GitBranch, incident.GitCommitHash ?? string.Empty, cancellationToken);
            incident.DeployStatus = deployId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Deploy trigger failed after HiveArena survival for incident {IncidentId}.", incident.Id);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return "validated";
    }

    /// <summary>
    /// Composes the PR body: HiveArena verdict plus the root-cause hypothesis
    /// and evidence counts from the incident's serialized RootCauseBundle.
    /// </summary>
    private static string BuildPullRequestBody(Incident incident, HiveArenaValidationCompletedMessage body)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Automated fix for incident `{incident.Id}`.");
        sb.AppendLine();
        sb.AppendLine($"**HiveArena validation:** Survival — {body.Summary}");

        if (!string.IsNullOrWhiteSpace(incident.RootCauseJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(incident.RootCauseJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("SummaryHypothesis", out var h) && h.ValueKind == JsonValueKind.String)
                {
                    sb.AppendLine();
                    sb.AppendLine("## Root-cause hypothesis");
                    sb.AppendLine(h.GetString());
                }
                if (root.TryGetProperty("RecommendedTrack", out var t) && t.ValueKind == JsonValueKind.String
                    && root.TryGetProperty("Confidence", out var c))
                {
                    sb.AppendLine();
                    sb.AppendLine($"Confidence: {c.GetDouble():0.##} · Recommended track: {t.GetString()}");
                }
            }
            catch (JsonException)
            {
                // Malformed RootCauseJson: the PR body simply omits the hypothesis.
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Defeat/Error: reopens the incident for the auto-workflow (the arena log is now
    /// in the message history and feeds the next fix attempt). After
    /// <see cref="MaxArenaDefeatsBeforeEscalation"/> failed validations the incident
    /// is escalated to an engineer instead of looping forever.
    /// </summary>
    private async Task<string> HandleFailedValidationAsync(
        HiveArenaValidationCompletedMessage body, CancellationToken cancellationToken)
    {
        var incident = await _db.Incidents
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == body.IncidentId, cancellationToken);
        if (incident is null)
            return "incident-missing";

        if (_tenantContext is TenantContext tc && !tc.IsResolved)
            tc.SetTenant(incident.TenantId);

        incident.ArenaValidationStatus = body.Outcome == 1 ? "Defeated" : "Error";

        var failures = (await _messageHistory.GetIncidentHistoryAsync(body.IncidentId, cancellationToken))
            .Count(m => m.AgentName == "HiveArena"
                     && (m.Content.Contains("outcome=Defeat") || m.Content.Contains("outcome=Error")));

        if (failures >= MaxArenaDefeatsBeforeEscalation)
        {
            incident.AssignedTo = "engineer";
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(
                "Incident {IncidentId} escalated to engineer after {Failures} failed HiveArena validations.",
                body.IncidentId, failures);
            return "escalated";
        }

        incident.Status = IncidentStatus.Open;
        incident.AssignedTo = "bot";
        incident.RequiresHumanApproval = false;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Incident {IncidentId} reopened for rework after HiveArena failure ({Failures}/{Max}).",
            body.IncidentId, failures, MaxArenaDefeatsBeforeEscalation);
        return "reopened";
    }
}
