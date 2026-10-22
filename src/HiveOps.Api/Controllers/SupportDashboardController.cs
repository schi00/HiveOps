using System.Xml.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HiveOps.Agents.Support;
using HiveOps.Api.Authentication;
using HiveOps.Application;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Support;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using HiveOps.Api.Hubs;

namespace HiveOps.Api.Controllers;

[ApiController]
[Route("api/support")]
[Authorize]
public sealed class SupportDashboardController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TenantContext _tenantContext;
    private readonly IncidentAnalysisEngine _analysisEngine;
    private readonly IGitService _gitService;
    private readonly IDeploymentService _deploymentService;
    private readonly ITenantDataFixerService _dataFixer;
    private readonly ISupervisionNotifier _notifier;
    private readonly IHubContext<SupervisionHub> _hub;
    private readonly ITenantConfigService _tenantConfigService;
    private readonly IIncidentChannelNotifier _channelNotifier;
    private readonly IEmbeddingService _embeddingService;
    private readonly IRowLevelSnapshotService _snapshotService;
    private readonly IIncidentAuditLedger _auditLedger;
    private readonly IDynamicConnectionStringResolver _connectionStringResolver;
    private readonly ILogger<SupportDashboardController> _logger;

    public SupportDashboardController(
        AppDbContext db,
        TenantContext tenantContext,
        IncidentAnalysisEngine analysisEngine,
        IGitService gitService,
        IDeploymentService deploymentService,
        ITenantDataFixerService dataFixer,
        ISupervisionNotifier notifier,
        IHubContext<SupervisionHub> hub,
        ITenantConfigService tenantConfigService,
        IIncidentChannelNotifier channelNotifier,
        IEmbeddingService embeddingService,
        IRowLevelSnapshotService snapshotService,
        IIncidentAuditLedger auditLedger,
        IDynamicConnectionStringResolver connectionStringResolver,
        ILogger<SupportDashboardController> logger)
    {
        _db = db;
        _tenantContext = tenantContext;
        _analysisEngine = analysisEngine;
        _gitService = gitService;
        _deploymentService = deploymentService;
        _dataFixer = dataFixer;
        _notifier = notifier;
        _hub = hub;
        _tenantConfigService = tenantConfigService;
        _channelNotifier = channelNotifier;
        _embeddingService = embeddingService;
        _snapshotService = snapshotService;
        _auditLedger = auditLedger;
        _connectionStringResolver = connectionStringResolver;
        _logger = logger;
    }

    private bool IsSuperAdmin => User.IsInRole(AppRoles.SuperAdmin);
    private bool IsAdmin => User.IsInRole(AppRoles.Admin);
    private bool IsPrivileged => IsSuperAdmin || IsAdmin;

    private Guid? ResolveTenantFromHeader()
    {
        // /api/support is exempt from TenantResolutionMiddleware, so when a privileged
        // dashboard user impersonates a tenant via X-Tenant-Id we must also mark the
        // TenantContext resolved — otherwise EF query filters keep CurrentTenantId = Empty
        // and tenant-scoped lookups silently return nothing.
        if (Request.Headers.TryGetValue("X-Tenant-Id", out var headerValue)
            && Guid.TryParse(headerValue.ToString(), out var tenantId)
            && !_tenantContext.IsResolved)
        {
            _tenantContext.SetTenant(tenantId);
            return tenantId;
        }
        return null;
    }

    private Guid? GetEffectiveTenantId()
    {
        if (_tenantContext.IsResolved) return _tenantContext.TenantId;
        var tenantClaim = User.FindFirstValue(AppClaimTypes.TenantId);
        if (Guid.TryParse(tenantClaim, out var claimTenantId))
        {
            _tenantContext.SetTenant(claimTenantId);
            return claimTenantId;
        }
        if (IsPrivileged) return ResolveTenantFromHeader();
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  INCIDENTS
    // ═══════════════════════════════════════════════════════════════════════

    [HttpGet("summary")]
    public async Task<ActionResult<SupportSummaryDto>> GetSummary(CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue && !IsPrivileged) return Unauthorized();

        var query = IsPrivileged
            ? _db.Incidents.IgnoreQueryFilters().AsNoTracking()
            : _db.Incidents.AsNoTracking();
        if (effectiveTenantId.HasValue)
            query = query.Where(i => i.TenantId == effectiveTenantId.Value);

        var totalOpen = await query.CountAsync(i => i.Status != IncidentStatus.Closed && i.Status != IncidentStatus.Resolved, ct);
        var totalResolved = await query.CountAsync(
            i => i.Status == IncidentStatus.Resolved || i.Status == IncidentStatus.Closed,
            ct);
        var totalCritical = await query.CountAsync(i => i.Severity == IncidentSeverity.Critical && i.Status != IncidentStatus.Closed && i.Status != IncidentStatus.Resolved, ct);

        var resolvedTimes = await query
            .Where(i => (i.Status == IncidentStatus.Resolved || i.Status == IncidentStatus.Closed) && i.ResolvedAt.HasValue)
            .Select(i => new { i.CreatedAt, ResolvedAt = i.ResolvedAt!.Value })
            .ToListAsync(ct);
        var avgResolution = resolvedTimes.Count == 0
            ? (double?)null
            : resolvedTimes.Average(i => (i.ResolvedAt - i.CreatedAt).TotalMinutes);

        return Ok(new SupportSummaryDto(totalOpen, totalResolved, totalCritical, (int)(avgResolution ?? 0.0)));
    }

    [HttpGet("incidents")]
    public async Task<ActionResult<IReadOnlyList<IncidentListItemDto>>> GetIncidents(
        [FromQuery] string? status = null,
        [FromQuery] string? severity = null,
        [FromQuery] string? category = null,
        [FromQuery] string? q = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue && !IsPrivileged) return Unauthorized();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        var query = IsPrivileged
            ? _db.Incidents.IgnoreQueryFilters().AsNoTracking()
            : _db.Incidents.AsNoTracking();
        if (effectiveTenantId.HasValue)
            query = query.Where(i => i.TenantId == effectiveTenantId.Value);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<IncidentStatus>(status, true, out var s))
            query = query.Where(i => i.Status == s);
        if (!string.IsNullOrWhiteSpace(severity) && Enum.TryParse<IncidentSeverity>(severity, true, out var sev))
            query = query.Where(i => i.Severity == sev);
        if (!string.IsNullOrWhiteSpace(category) && Enum.TryParse<IncidentCategory>(category, true, out var c))
            query = query.Where(i => i.Category == c);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(i => i.Title.Contains(q) || i.Description.Contains(q));

        var items = await query
            .OrderByDescending(i => i.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new IncidentListItemDto(
                i.Id,
                i.Title,
                i.Category.ToString(),
                i.Severity.ToString(),
                i.Status.ToString(),
                i.AssignedTo,
                i.CreatedAt,
                i.UpdatedAt,
                i.DeployStatus,
                _db.DiagnosticLogs
                    .Where(l => l.IncidentId == i.Id && l.StepName == "ci.callback")
                    .OrderByDescending(l => l.CreatedAt)
                    .Select(l => l.Result)
                    .FirstOrDefault()))
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("notifications")]
    public async Task<ActionResult<IReadOnlyList<object>>> GetNotifications(CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue && !IsPrivileged) return Unauthorized();

        var query = IsPrivileged
            ? _db.Incidents.IgnoreQueryFilters().AsNoTracking()
            : _db.Incidents.AsNoTracking();
        if (effectiveTenantId.HasValue)
            query = query.Where(i => i.TenantId == effectiveTenantId.Value);

        var items = await query
            .Where(i => i.CreatedAt > DateTimeOffset.UtcNow.AddHours(-24))
            .OrderByDescending(i => i.CreatedAt)
            .Take(20)
            .Select(i => new { id = i.Id, type = "incident", title = i.Title, severity = i.Severity.ToString(), createdAt = i.CreatedAt, read = false })
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("incidents")]
    public async Task<ActionResult<IncidentDetailDto>> CreateIncident(
        [FromBody] CreateIncidentRequest request,
        CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue) return Unauthorized();

        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            TenantId = effectiveTenantId.Value,
            ConversationId = request.ConversationId ?? Guid.Empty,
            Title = request.Title,
            Description = request.Description,
            Category = Enum.TryParse<IncidentCategory>(request.Category, true, out var reqCat) ? reqCat : IncidentCategory.Other,
            Severity = Enum.TryParse<IncidentSeverity>(request.Severity, true, out var reqSev) ? reqSev : IncidentSeverity.Low,
            Status = IncidentStatus.Open,
            AssignedTo = "bot",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.Incidents.Add(incident);
        await _db.SaveChangesAsync(ct);

        IncidentAnalysisResult? analysis = null;
        try
        {
            analysis = await _analysisEngine.AnalyzeAsync(
                effectiveTenantId.Value, request.Description, incident.Id, ct);
            IncidentTriageApplier.ApplyAnalysis(incident, analysis);

            var oldest = await _db.Incidents
                .Where(i => i.TenantId == effectiveTenantId.Value
                            && i.Fingerprint == incident.Fingerprint
                            && i.Status != IncidentStatus.Closed
                            && i.Status != IncidentStatus.Resolved)
                .OrderBy(i => i.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (oldest is not null && oldest.Id != incident.Id)
            {
                oldest.Description += $"\n[Merged duplicate {incident.Id}]\n{incident.Description}";
                oldest.OccurrenceCount += incident.OccurrenceCount;
                oldest.UpdatedAt = DateTimeOffset.UtcNow;
                _db.Incidents.Remove(incident);
                incident = oldest;
            }

            incident.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            HttpContext.RequestServices
                .GetRequiredService<ILogger<SupportDashboardController>>()
                .LogWarning(ex, "LLM triage failed for incident {IncidentId}; continuing without analysis.", incident.Id);
        }

        await _notifier.NotifyIncidentCreatedAsync(effectiveTenantId.Value, incident.Id, incident.Title, incident.Severity.ToString(), ct);

        return Ok(MapToDetail(incident, analysis));
    }

    [HttpGet("incidents/{id:guid}")]
    public async Task<ActionResult<IncidentDetailDto>> GetIncident(Guid id, CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue && !IsPrivileged) return Unauthorized();

        var query = _db.Incidents.AsNoTracking().Where(i => i.Id == id);
        if (!IsPrivileged)
            query = query.Where(i => i.TenantId == effectiveTenantId!.Value);

        var incident = await query.FirstOrDefaultAsync(ct);
        if (incident is null) return NotFound();

        var logs = await _db.DiagnosticLogs.AsNoTracking()
            .Where(l => l.IncidentId == id)
            .OrderBy(l => l.CreatedAt)
            .Select(l => new TimelineItemDto(l.StepName, l.Result, l.IsSuccess, l.CreatedAt))
            .ToListAsync(ct);

        var attachments = await _db.IncidentAttachments.AsNoTracking()
            .Where(a => a.IncidentId == id)
            .Select(a => new AttachmentDto(a.Id, a.Type.ToString(), a.FileName, a.CreatedAt))
            .ToListAsync(ct);

        // Extract latest CI info (runId, logUrl)
        string? runId = null, logUrl = null;
        foreach (var tl in logs.Where(t => string.Equals(t.Step, "ci.callback", StringComparison.OrdinalIgnoreCase)).Reverse())
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(tl.Result ?? "{}");
                var root = doc.RootElement;
                runId = root.TryGetProperty("runId", out var r) ? r.GetString() : null;
                logUrl = root.TryGetProperty("logUrl", out var u) ? u.GetString() : null;
                if (!string.IsNullOrWhiteSpace(runId) || !string.IsNullOrWhiteSpace(logUrl)) break;
            }
            catch { /* ignore parse errors */ }
        }

        return Ok(new IncidentDetailDto(
            incident.Id,
            incident.Title,
            incident.Description,
            incident.Category.ToString(),
            incident.Severity.ToString(),
            incident.Status.ToString(),
            incident.AssignedTo,
            incident.GitBranch,
            incident.GitCommitHash,
            incident.ResolutionNotes,
            incident.CreatedAt,
            incident.UpdatedAt,
            incident.ResolvedAt,
            logs,
            attachments,
            null,
            incident.DeployStatus,
            runId,
            logUrl,
            incident.FinalUserResponse,
            incident.TechnicalDiagnosticNote,
            incident.TriageConfidence,
            incident.Fingerprint,
            incident.OccurrenceCount,
            incident.ResolutionMode.ToString(),
            incident.EligibleForEffectivenessKpi,
            incident.RootCauseJson,
            incident.VerificationQuery,
            incident.VerificationPassed,
            incident.PullRequestUrl,
            incident.ArenaValidationStatus));
    }

    [HttpGet("incidents/{id:guid}/messages")]
    public async Task<ActionResult<IReadOnlyList<MessageDto>>> GetIncidentMessages(Guid id, CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue && !IsPrivileged) return Unauthorized();

        var incident = await _db.Incidents.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null) return NotFound();

        if (!IsPrivileged && incident.TenantId != effectiveTenantId!.Value)
            return Forbid();

        var messages = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == incident.ConversationId && m.TenantId == incident.TenantId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new MessageDto(m.Id, m.Role.ToString(), m.Content, m.CreatedAt))
            .ToListAsync(ct);

        return Ok(messages);
    }

    [HttpPost("incidents/{id:guid}/attachments")]
    public async Task<ActionResult> AddAttachment(Guid id, [FromBody] AddAttachmentRequest request, CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue) return Unauthorized();

        var incident = await _db.Incidents.FirstOrDefaultAsync(i => i.Id == id && i.TenantId == effectiveTenantId.Value, ct);
        if (incident is null) return NotFound();

        _db.IncidentAttachments.Add(new IncidentAttachment
        {
            Id = Guid.NewGuid(),
            TenantId = effectiveTenantId.Value,
            IncidentId = id,
            Type = request.Type,
            FileName = request.FileName,
            Content = request.Content
        });
        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "Attachment added." });
    }

    [HttpPost("incidents/{id:guid}/approve-db-fix")]
    public async Task<ActionResult> ApproveDbFix(Guid id, CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue) return Unauthorized();

        var incident = await _db.Incidents
            .Include(i => i.Attachments)
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == effectiveTenantId.Value, ct);
        if (incident is null) return NotFound();

        var scriptAttachment = incident.Attachments
            .FirstOrDefault(a => a.Type == IncidentAttachmentType.SqlScript);
        if (scriptAttachment is null)
            return BadRequest(new { message = "No pending DB fix script found." });

        // Safety check (already validated at creation, but re-check before execution)
        if (!SafetyValidator.IsSafeSql(scriptAttachment.Content))
        {
            var reason = SafetyValidator.GetRejectionReason(scriptAttachment.Content);
            return BadRequest(new { message = $"Script failed safety validation: {reason}" });
        }

        // Execute the safe SQL script against the tenant database
        var fixResult = await _dataFixer.ExecuteSafeSqlAsync(
            incident.TenantId,
            scriptAttachment.Content,
            id,
            ct);

        if (!fixResult.Success)
        {
            return StatusCode(500, new { message = $"DB fix execution failed: {fixResult.Message}" });
        }

        incident.Status = IncidentStatus.InProgress;
        incident.ResolutionNotes = (incident.ResolutionNotes ?? "") + $"\nDB fix executed: {scriptAttachment.FileName} | {fixResult.Message} | Rows affected: {fixResult.RowsAffected}";
        incident.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        await NotifyChannelSafelyAsync(incident.TenantId, incident.Id,
            $"✅ Se aplicó una corrección de datos en tu ticket #{incident.Id.ToString("N")[..8]} ({fixResult.RowsAffected} registros afectados). Estamos verificando que todo funcione correctamente.", ct);

        return Ok(new { message = "DB fix executed successfully.", fixResult.RowsAffected, fixResult.Message });
    }

    [HttpPost("incidents/{id:guid}/reject-fix")]
    public async Task<ActionResult> RejectFix(Guid id, [FromBody] RejectFixRequest request, CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue) return Unauthorized();

        var incident = await _db.Incidents
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == effectiveTenantId.Value, ct);
        if (incident is null) return NotFound();

        incident.Status = IncidentStatus.Open;
        incident.ResolutionNotes = (incident.ResolutionNotes ?? "") + $"\nFix rejected: {request.Reason}";
        incident.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await NotifyChannelSafelyAsync(incident.TenantId, incident.Id,
            "Se descartó el cambio propuesto para tu ticket. Nuestro equipo lo revisará manualmente.", ct);

        return Ok(new { message = "Fix rejected. Incident reopened." });
    }

    [HttpPost("incidents/{id:guid}/rollback")]
    public async Task<ActionResult> Rollback(Guid id, CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue && !IsPrivileged) return Unauthorized();

        // Only Admin/SuperAdmin can trigger rollback
        if (!IsPrivileged) return StatusCode(StatusCodes.Status403Forbidden);

        var incident = await _db.Incidents
            .FirstOrDefaultAsync(i => i.Id == id && (IsPrivileged || i.TenantId == effectiveTenantId!.Value), ct);
        if (incident is null) return NotFound();

        // Plan gating: tenant must have rollback capability
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == incident.TenantId, ct);
        if (tenant is null) return NotFound();
        if (!tenant.HasRollbackCapability)
            return StatusCode(StatusCodes.Status403Forbidden, "El plan actual no permite realizar rollback.");

        // Allow rollback only after SUCCESS or FAILED deploy states
        var st = incident.DeployStatus ?? string.Empty;
        if (!string.Equals(st, "SUCCESS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(st, "FAILED", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Rollback is only available after a SUCCESS or FAILED deploy.");

        // Find latest successful ci.callback (previous good state)
        var logs = await _db.DiagnosticLogs.AsNoTracking()
            .Where(l => l.IncidentId == id && l.StepName == "ci.callback" && l.IsSuccess)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(ct);

        string? prevRunId = null;
        string? prevLogUrl = null;
        if (logs.Count > 0)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(logs[0].Result ?? "{}");
                var root = doc.RootElement;
                prevRunId = root.TryGetProperty("runId", out var r) ? r.GetString() : null;
                prevLogUrl = root.TryGetProperty("logUrl", out var u) ? u.GetString() : null;
            }
            catch { }
        }

        await _db.DiagnosticLogs.AddAsync(new DiagnosticLog
        {
            TenantId = incident.TenantId,
            IncidentId = incident.Id,
            StepName = "rollback.requested",
            Result = System.Text.Json.JsonSerializer.Serialize(new
            {
                previousRunId = prevRunId,
                previousLogUrl = prevLogUrl,
                requestedBy = User.Identity?.Name ?? "admin",
                at = DateTimeOffset.UtcNow
            }),
            IsSuccess = true
        }, ct);
        await _db.SaveChangesAsync(ct);

        // Notify realtime
        await _hub.Clients.Group($"tenant:{incident.TenantId}")
            .SendAsync("DeploymentUpdated", new { incidentId = incident.Id, status = "ROLLBACK_REQUESTED", progress = 0, eta = 0, at = DateTimeOffset.UtcNow, runId = prevRunId, logUrl = prevLogUrl }, ct);

        return Accepted(new { message = "Rollback requested.", previousRunId = prevRunId });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  REMEDIATION SNAPSHOTS & AUDIT CHAIN (WP-5)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Row-level before/after snapshots captured for an incident's DB
    /// remediation, including the generated inverse SQL for surgical rollback.
    /// </summary>
    [HttpGet("incidents/{id:guid}/snapshots")]
    public async Task<ActionResult<IReadOnlyList<IncidentSnapshotDto>>> GetIncidentSnapshots(
        Guid id, CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue && !IsPrivileged) return Unauthorized();

        var incidentQuery = _db.Incidents.AsNoTracking().Where(i => i.Id == id);
        if (!IsPrivileged)
            incidentQuery = incidentQuery.Where(i => i.TenantId == effectiveTenantId!.Value);
        var incident = await incidentQuery.FirstOrDefaultAsync(ct);
        if (incident is null) return NotFound();

        // Privileged users often run without a resolved TenantContext on this
        // exempt path — drop the query filter but stay scoped to the incident's
        // tenant via an explicit predicate.
        var snapshotsQuery = IsPrivileged
            ? _db.IncidentRemediationSnapshots.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.IncidentId == id && s.TenantId == incident.TenantId)
            : _db.IncidentRemediationSnapshots.AsNoTracking()
                .Where(s => s.IncidentId == id);

        var items = await snapshotsQuery
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new IncidentSnapshotDto(
                s.Id,
                s.TargetTable,
                s.AffectedRowCount,
                s.IsReverted,
                s.BeforeRowsJson,
                s.AfterRowsJson,
                s.InverseSqlScript,
                s.CreatedAt,
                s.RevertedAt,
                s.RevertedBy))
            .ToListAsync(ct);

        return Ok(items);
    }

    /// <summary>
    /// Executes the snapshot's inverse SQL on the tenant database. Privileged
    /// only; the revert is sealed in the incident's audit chain.
    /// </summary>
    [HttpPost("incidents/{id:guid}/snapshots/{snapshotId:guid}/revert")]
    public async Task<ActionResult> RevertSnapshot(Guid id, Guid snapshotId, CancellationToken ct)
    {
        if (!IsPrivileged) return StatusCode(StatusCodes.Status403Forbidden);
        var effectiveTenantId = GetEffectiveTenantId();

        var incident = await _db.Incidents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null) return NotFound();

        // A privileged user impersonating a tenant stays inside that tenant.
        if (effectiveTenantId.HasValue && incident.TenantId != effectiveTenantId.Value)
            return Forbid();

        var snapshot = await _db.IncidentRemediationSnapshots.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == snapshotId && s.IncidentId == incident.Id && s.TenantId == incident.TenantId, ct);
        if (snapshot is null) return NotFound();
        if (snapshot.IsReverted)
            return BadRequest(new { message = "Snapshot already reverted." });

        string connectionString;
        try
        {
            await _connectionStringResolver.WarmCacheAsync(incident.TenantId, ct);
            connectionString = _connectionStringResolver.Resolve(incident.TenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not resolve tenant connection for snapshot revert (incident {IncidentId}).", id);
            return StatusCode(500, new { message = "Could not resolve the tenant database connection." });
        }

        var revertedBy = User.Identity?.Name ?? "admin";

        int rowsAffected;
        try
        {
            rowsAffected = await _snapshotService.RevertSnapshotAsync(snapshot, connectionString, revertedBy, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot {SnapshotId} revert failed (incident {IncidentId}).", snapshotId, id);
            return StatusCode(500, new { message = $"Snapshot revert failed: {ex.Message}" });
        }

        try
        {
            await _auditLedger.AppendAsync(
                incident.TenantId,
                incident.Id,
                "Rollback",
                revertedBy,
                new
                {
                    snapshotId = snapshot.Id,
                    snapshot.TargetTable,
                    rowsAffected
                },
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audit append (Rollback) failed for incident {IncidentId}.", id);
        }

        await _hub.Clients.Group($"tenant:{incident.TenantId}")
            .SendAsync("SnapshotReverted", new { incidentId = incident.Id, snapshotId, rowsAffected, at = DateTimeOffset.UtcNow }, ct);

        return Ok(new { message = "Snapshot reverted.", rowsAffected });
    }

    /// <summary>
    /// Returns the incident's SHA-256 hash-chained audit entries plus the
    /// result of a full chain verification.
    /// </summary>
    [HttpGet("incidents/{id:guid}/audit-chain")]
    public async Task<ActionResult> GetAuditChain(Guid id, CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue && !IsPrivileged) return Unauthorized();

        var incidentQuery = _db.Incidents.AsNoTracking().Where(i => i.Id == id);
        if (!IsPrivileged)
            incidentQuery = incidentQuery.Where(i => i.TenantId == effectiveTenantId!.Value);
        var incident = await incidentQuery.FirstOrDefaultAsync(ct);
        if (incident is null) return NotFound();

        var entriesQuery = IsPrivileged
            ? _db.IncidentAuditEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.IncidentId == id && e.TenantId == incident.TenantId)
            : _db.IncidentAuditEntries.AsNoTracking()
                .Where(e => e.IncidentId == id);

        var entries = await entriesQuery
            .OrderBy(e => e.SequenceNumber)
            .Select(e => new AuditEntryDto(
                e.SequenceNumber,
                e.EventType,
                e.Actor,
                e.PayloadJson,
                e.EntryHash,
                e.PreviousHash,
                e.CreatedAt))
            .ToListAsync(ct);

        var verification = await _auditLedger.VerifyChainAsync(incident.TenantId, incident.Id, ct);

        return Ok(new
        {
            chainValid = verification.IsValid,
            verification.TotalEntries,
            verification.BrokenAtEntryId,
            entries
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MERGE QUEUE (SuperAdmin only)
    // ═══════════════════════════════════════════════════════════════════════

    [HttpGet("merge-queue")]
    public async Task<ActionResult<IReadOnlyList<MergeRequestDto>>> GetMergeQueue(CancellationToken ct)
    {
        if (!IsPrivileged) return StatusCode(StatusCodes.Status403Forbidden);
        var prs = await _db.Incidents.IgnoreQueryFilters().AsNoTracking()
            .Where(i => !string.IsNullOrEmpty(i.GitBranch) && i.Status == IncidentStatus.InProgress)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new MergeRequestDto(
                i.Id,
                i.Title,
                i.GitBranch!,
                i.GitCommitHash ?? "unknown",
                i.TenantId,
                i.CreatedAt))
            .ToListAsync(ct);

        return Ok(prs);
    }

    [HttpPost("merge-queue/{incidentId:guid}/approve")]
    public async Task<ActionResult> ApproveMerge(Guid incidentId, CancellationToken ct)
    {
        if (!IsPrivileged) return StatusCode(StatusCodes.Status403Forbidden);
        // IgnoreQueryFilters: privileged dashboard users often run without a resolved
        // TenantContext (the /api/support path is exempt from TenantResolutionMiddleware),
        // which would make CurrentTenantId = Guid.Empty and this lookup always miss.
        var incident = await _db.Incidents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == incidentId, ct);
        if (incident is null) return NotFound();

        if (string.IsNullOrWhiteSpace(incident.GitBranch))
            return BadRequest(new { message = "No branch associated with this incident." });

        if (!await TenantDeployPolicy.IsManualDeployAllowedAsync(_tenantConfigService, incident.TenantId, ct))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Manual deploy is disabled for this tenant (DeployGit.ManualDeployAllowed)." });
        }

        // Merge PR via Git provider API
        var mergeResult = await _gitService.MergePullRequestAsync(incident.TenantId, incident.GitBranch, ct);

        // After merge, trigger deploy
        var pipelineHealthy = await _deploymentService.IsPipelineHealthyAsync(ct);
        if (!pipelineHealthy)
        {
            return StatusCode(503, new { message = "Merge succeeded but pipeline is unhealthy. Deploy blocked." });
        }

        var deployId = await _deploymentService.TriggerDeployAsync("main", mergeResult, ct);

        incident.DeployStatus = deployId;
        incident.Status = IncidentStatus.Resolved;
        incident.ResolvedAt = DateTimeOffset.UtcNow;
        incident.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await NotifyChannelSafelyAsync(incident.TenantId, incident.Id,
            $"✅ Se aprobó y desplegó la corrección de tu ticket #{incident.Id.ToString("N")[..8]}. Te confirmamos en breve.", ct);

        return Ok(new { message = "Merged and deployed.", deployId });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SYSTEM HEALTH (SuperAdmin only)
    // ═══════════════════════════════════════════════════════════════════════

    [HttpGet("system-health")]
    public async Task<ActionResult<SystemHealthDto>> GetSystemHealth(CancellationToken ct)
    {
        if (!IsPrivileged) return StatusCode(StatusCodes.Status403Forbidden);
        var dbHealthy = await _db.Database.CanConnectAsync(ct);
        var pipelineHealthy = await _deploymentService.IsPipelineHealthyAsync(ct);
        var lastTestRun = "unknown"; // would be stored in a settings table or retrieved from CI/CD

        return Ok(new SystemHealthDto(dbHealthy, pipelineHealthy, lastTestRun));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  KNOWLEDGE BASE
    // ═══════════════════════════════════════════════════════════════════════

    [HttpGet("kb")]
    public async Task<ActionResult<IReadOnlyList<KbArticleDto>>> SearchKb(
        [FromQuery] string? q = null,
        [FromQuery] int take = 20,
        CancellationToken ct = default)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue && !IsPrivileged) return Unauthorized();
        take = Math.Clamp(take, 1, 100);

        var query = _db.KbArticles.AsNoTracking();
        
        // Privileged users see all KB articles, regular users see only their tenant's
        if (!IsPrivileged && effectiveTenantId.HasValue)
        {
            query = query.Where(a => a.TenantId == effectiveTenantId.Value);
        }
        
        query = query.Where(a => a.IsPublished);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.ToLowerInvariant();
            query = query.Where(a =>
                a.Title.ToLower().Contains(term) ||
                a.Content.ToLower().Contains(term) ||
                a.Tags.ToLower().Contains(term));
        }

        var items = await query
            .OrderByDescending(a => a.UpdatedAt)
            .Take(take)
            .Select(a => new KbArticleDto(a.Id, a.Title, a.Category, a.Tags, a.CreatedAt))
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("kb")]
    [Authorize(Roles = AppRoles.SuperAdmin)]
    public async Task<ActionResult> CreateKbArticle([FromBody] CreateKbRequest request, CancellationToken ct)
    {
        // GetEffectiveTenantId resolves either the JWT tenant claim or, for privileged
        // dashboard users, the X-Tenant-Id header (TenantContext is not resolved by the
        // middleware because /api/support is an exempt path).
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue) return Unauthorized();

        var article = new KbArticle
        {
            Id = Guid.NewGuid(),
            TenantId = effectiveTenantId.Value,
            Title = request.Title,
            Content = request.Content,
            Category = request.Category,
            Tags = request.Tags,
            ResolutionSteps = request.ResolutionSteps,
            IsPublished = request.IsPublished,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await TryGenerateArticleEmbeddingAsync(article, ct);

        _db.KbArticles.Add(article);
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = article.Id });
    }

    /// <summary>Auto-resolution effectiveness per <c>docs/architecture/incident-effectiveness-kpi.md</c>.</summary>
    [HttpGet("effectiveness-metrics")]
    public async Task<ActionResult<object>> GetEffectivenessMetrics([FromQuery] int windowDays = 28, CancellationToken ct = default)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue && !IsPrivileged) return Unauthorized();

        windowDays = Math.Clamp(windowDays, 1, 365);
        var since = DateTimeOffset.UtcNow.AddDays(-windowDays);

        var query = IsPrivileged
            ? _db.Incidents.IgnoreQueryFilters().AsNoTracking()
            : _db.Incidents.AsNoTracking();
        if (effectiveTenantId.HasValue)
            query = query.Where(i => i.TenantId == effectiveTenantId.Value);

        var resolvedEligible = query.Where(i =>
            i.EligibleForEffectivenessKpi
            && i.ResolvedAt >= since
            && (i.Status == IncidentStatus.Closed || i.Status == IncidentStatus.Resolved));

        var denominator = await resolvedEligible.CountAsync(ct);
        var numerator = await resolvedEligible.CountAsync(
            i => i.ResolutionMode == IncidentResolutionMode.AutoCode
                || i.ResolutionMode == IncidentResolutionMode.AutoDatabase
                || i.ResolutionMode == IncidentResolutionMode.Hybrid,
            ct);

        double? rate = denominator == 0 ? null : (double)numerator / denominator;

        return Ok(new
        {
            windowDays,
            since,
            denominatorEligibleResolved = denominator,
            numeratorAutoResolved = numerator,
            autoResolutionEffectiveness = rate,
            target = 0.70
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sends a user-facing progress message to the channel where the incident was
    /// reported. Notification failures must never break the dashboard endpoints.
    /// </summary>
    private async Task NotifyChannelSafelyAsync(Guid tenantId, Guid incidentId, string message, CancellationToken ct)
    {
        try
        {
            await _channelNotifier.NotifyIncidentProgressAsync(tenantId, incidentId, message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Channel notification failed for incident {IncidentId}.", incidentId);
        }
    }

    /// <summary>
    /// Best-effort embedding for KB semantic search. Failures leave the article
    /// without a vector; keyword search remains the fallback.
    /// </summary>
    private async Task TryGenerateArticleEmbeddingAsync(KbArticle article, CancellationToken ct)
    {
        try
        {
            var vector = await _embeddingService.GenerateEmbeddingAsync(
                $"{article.Title}\n{article.Content}\n{article.ResolutionSteps}", ct);
            if (vector is { Length: > 0 })
                article.EmbeddingVector = EmbeddingMath.FloatsToLittleEndianBytes(vector);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding generation failed for KB article {ArticleId}; saving without vector.", article.Id);
        }
    }

    private static IncidentDetailDto MapToDetail(Incident i, IncidentAnalysisResult? analysis = null)
    {
        return new IncidentDetailDto(
            i.Id,
            i.Title,
            i.Description,
            i.Category.ToString(),
            i.Severity.ToString(),
            i.Status.ToString(),
            i.AssignedTo,
            i.GitBranch,
            i.GitCommitHash,
            i.ResolutionNotes,
            i.CreatedAt,
            i.UpdatedAt,
            i.ResolvedAt,
            [],
            [],
            analysis is null ? null : analysis.Reasoning,
            i.DeployStatus,
            null,
            null,
            i.FinalUserResponse,
            i.TechnicalDiagnosticNote,
            i.TriageConfidence,
            i.Fingerprint,
            i.OccurrenceCount,
            i.ResolutionMode.ToString(),
            i.EligibleForEffectivenessKpi,
            i.RootCauseJson,
            i.VerificationQuery,
            i.VerificationPassed,
            i.PullRequestUrl,
            i.ArenaValidationStatus);
    }

    /// <summary>
    /// Dev-only endpoint: reads the latest TRX test-results file and creates incidents
    /// with natural-language descriptions so the bot (LLM) can analyse them.
    /// Protected by X-Dev-Key header.
    /// </summary>
    [HttpPost("incidents/from-test-results")]
    [AllowAnonymous]
    public async Task<ActionResult<List<IncidentDetailDto>>> CreateIncidentsFromTestResults(
        [FromHeader(Name = "X-Dev-Key")] string? devKey,
        CancellationToken ct)
    {
        const string expectedDevKey = "hiveops-dev-2026";
        if (devKey != expectedDevKey)
            return Unauthorized(new { error = "Missing or invalid X-Dev-Key header" });

        var trxPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "tests", "HiveOps.UnitTests", "TestResults", "unit-tests.trx");

        if (!System.IO.File.Exists(trxPath))
            trxPath = Path.Combine(Directory.GetCurrentDirectory(), "tests", "HiveOps.UnitTests", "TestResults", "unit-tests.trx");

        if (!System.IO.File.Exists(trxPath))
            return BadRequest(new { error = "TRX file not found", searchedPath = trxPath });

        var doc = XDocument.Load(trxPath);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

        var failedTests = doc.Descendants(ns + "UnitTestResult")
            .Where(r => (string?)r.Attribute("outcome") == "Failed")
            .ToList();

        if (failedTests.Count == 0)
            return Ok(new { message = "No failed tests found in TRX", incidentsCreated = 0 });

        var devTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var created = new List<IncidentDetailDto>();

        foreach (var test in failedTests)
        {
            var testName = (string?)test.Attribute("testName") ?? "unknown-test";
            var message = test.Element(ns + "Output")?.Element(ns + "ErrorInfo")?.Element(ns + "Message")?.Value ?? "No error message";
            var stackTrace = test.Element(ns + "Output")?.Element(ns + "ErrorInfo")?.Element(ns + "StackTrace")?.Value ?? "";

            var severity = IncidentSeverity.Medium;
            if (message.Contains("Regex", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("NullReference", StringComparison.OrdinalIgnoreCase))
                severity = IncidentSeverity.Critical;
            else if (message.Contains("Assert", StringComparison.OrdinalIgnoreCase))
                severity = IncidentSeverity.High;

            var stackLines = string.Join("\n", stackTrace.Split('\n').Take(8));
            var description = "El test automatizado '" + testName + "' fallo durante la ejecucion de la suite de pruebas.\n\n" +
                "Error tecnico:\n" + message + "\n\n" +
                "Stack trace resumido:\n" + stackLines + "\n\n" +
                "Impacto: Este fallo indica una regresion en el codigo que podria afectar la estabilidad del sistema en produccion. Se recomienda revision inmediata del componente afectado.";

            var incident = new Incident
            {
                Id = Guid.NewGuid(),
                TenantId = devTenantId,
                ConversationId = Guid.Empty,
                Title = "[TEST-FAIL] " + testName,
                Description = description,
                Category = IncidentCategory.Code,
                Severity = severity,
                Status = IncidentStatus.Open,
                AssignedTo = "bot",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _db.Incidents.Add(incident);
            await _db.SaveChangesAsync(ct);

            IncidentAnalysisResult? analysis = null;
            try
            {
                analysis = await _analysisEngine.AnalyzeAsync(devTenantId, description, incident.Id, ct);
                IncidentTriageApplier.ApplyAnalysis(incident, analysis);
                incident.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                HttpContext.RequestServices
                    .GetRequiredService<ILogger<SupportDashboardController>>()
                    .LogWarning(ex, "LLM triage failed for test-incident {IncidentId}; continuing without analysis.", incident.Id);
            }

            created.Add(MapToDetail(incident, analysis));
        }

        await _notifier.NotifyIncidentCreatedAsync(devTenantId, created.Last().Id, $"Created {created.Count} incidents from test failures", created.Last().Severity, ct);

        return Ok(created);
    }

    /// <summary>
    /// Dev-only endpoint for Sandpit: creates a single incident from a fault simulation.
    /// Protected by X-Dev-Key header.
    /// </summary>
    [HttpPost("incidents/from-sandpit")]
    [AllowAnonymous]
    public async Task<ActionResult<IncidentDetailDto>> CreateIncidentFromSandpit(
        [FromHeader(Name = "X-Dev-Key")] string? devKey,
        [FromBody] CreateIncidentFromSandpitRequest request,
        CancellationToken ct)
    {
        const string expectedDevKey = "hiveops-dev-2026";
        if (devKey != expectedDevKey)
            return Unauthorized(new { error = "Missing or invalid X-Dev-Key header" });

        // Use the real Sandpit tenant (ID: 22222222-2222-2222-2222-222222222222)
        var sandpitTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        // Set tenant context for RLS session context - MUST happen before any DB operation
        // The TenantSessionContextInterceptor will automatically set the app.tenant_id GUC when DB operations occur
        if (_tenantContext is TenantContext tc && !tc.IsResolved)
        {
            tc.SetTenant(sandpitTenantId);
        }

        // Determine category based on incident title (simple rules for Sandpit errors)
        var category = IncidentCategory.Other;
        var severity = IncidentSeverity.Medium;
        
        var titleLower = request.Title.ToLowerInvariant();
        var descLower = request.Description.ToLowerInvariant();
        
        if (titleLower.Contains("db") || titleLower.Contains("database") || 
            titleLower.Contains("corrupt") || titleLower.Contains("sql") ||
            descLower.Contains("tabla") || descLower.Contains("registros"))
        {
            category = IncidentCategory.Database;
            severity = IncidentSeverity.High;
        }
        else if (titleLower.Contains("code") || titleLower.Contains("cwe") ||
                 titleLower.Contains("bug") || titleLower.Contains("vulnerability"))
        {
            category = IncidentCategory.Code;
            severity = IncidentSeverity.High;
        }
        else if (titleLower.Contains("timeout") || titleLower.Contains("performance"))
        {
            category = IncidentCategory.Infrastructure;
            severity = IncidentSeverity.Medium;
        }

        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            TenantId = sandpitTenantId,
            ConversationId = null,
            Title = request.Title,
            Description = request.Description,
            Category = category,
            Severity = severity,
            Status = IncidentStatus.Open,
            AssignedTo = "bot",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.Incidents.Add(incident);
        await _db.SaveChangesAsync(ct);

        // Try LLM analysis but don't fail if it doesn't work
        IncidentAnalysisResult? analysis = null;
        try
        {
            analysis = await _analysisEngine.AnalyzeAsync(sandpitTenantId, request.Description, incident.Id, ct);
            IncidentTriageApplier.ApplyAnalysis(incident, analysis);
            incident.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            HttpContext.RequestServices
                .GetRequiredService<ILogger<SupportDashboardController>>()
                .LogWarning(ex, "LLM triage failed for sandpit incident {IncidentId}; using rule-based classification.", incident.Id);
            // Continue with rule-based classification already set
        }

        await _notifier.NotifyIncidentCreatedAsync(sandpitTenantId, incident.Id, incident.Title, incident.Severity.ToString(), ct);

        return Ok(MapToDetail(incident, analysis));
    }
}

    // ═══════════════════════════════════════════════════════════════════════════
    //  DTOs
    // ═══════════════════════════════════════════════════════════════════════════

public sealed record SupportSummaryDto(int OpenIncidents, int ResolvedThisMonth, int CriticalOpen, int AvgResolutionMinutes);
public sealed record IncidentListItemDto(Guid Id, string Title, string Category, string Severity, string Status, string AssignedTo, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? DeployStatus, string? LastCiResult);
public sealed record TimelineItemDto(string Step, string Result, bool IsSuccess, DateTimeOffset CreatedAt);
public sealed record AttachmentDto(Guid Id, string Type, string FileName, DateTimeOffset CreatedAt);
public sealed record IncidentDetailDto(
    Guid Id,
    string Title,
    string Description,
    string Category,
    string Severity,
    string Status,
    string AssignedTo,
    string? GitBranch,
    string? GitCommitHash,
    string? ResolutionNotes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt,
    IReadOnlyList<TimelineItemDto> Timeline,
    IReadOnlyList<AttachmentDto> Attachments,
    string? LlmReasoning = null,
    string? DeployStatus = null,
    string? DeployRunId = null,
    string? DeployLogUrl = null,
    string? FinalUserResponse = null,
    string? TechnicalDiagnosticNote = null,
    double? TriageConfidence = null,
    string? Fingerprint = null,
    int OccurrenceCount = 1,
    string? ResolutionMode = null,
    bool EligibleForEffectivenessKpi = false,
    string? RootCauseJson = null,
    string? VerificationQuery = null,
    bool? VerificationPassed = null,
    string? PullRequestUrl = null,
    string? ArenaValidationStatus = null);

public sealed record MergeRequestDto(Guid IncidentId, string Title, string Branch, string CommitHash, Guid TenantId, DateTimeOffset CreatedAt);
public sealed record IncidentSnapshotDto(Guid Id, string TargetTable, int AffectedRowCount, bool IsReverted, string BeforeRowsJson, string AfterRowsJson, string InverseSqlScript, DateTimeOffset CreatedAt, DateTimeOffset? RevertedAt, string? RevertedBy);
public sealed record AuditEntryDto(int SequenceNumber, string EventType, string Actor, string PayloadJson, string EntryHash, string PreviousHash, DateTimeOffset CreatedAt);
public sealed record SystemHealthDto(bool DbHealthy, bool PipelineHealthy, string LastTestRun);
public sealed record KbArticleDto(Guid Id, string Title, string Category, string Tags, DateTimeOffset CreatedAt);

public sealed record CreateIncidentRequest(Guid? ConversationId, string Title, string Description, string? Category, string? Severity);
public sealed record CreateIncidentFromSandpitRequest(
    string Title,
    string Description,
    bool? IsSynthetic = null,
    Guid? SimulationRunId = null,
    string? ScenarioId = null,
    string? FaultType = null,
    int? Seed = null,
    Guid? ArenaEnvironmentId = null);
public sealed record AddAttachmentRequest(IncidentAttachmentType Type, string FileName, string Content);
public sealed record RejectFixRequest(string Reason);
public sealed record MessageDto(Guid Id, string Role, string Content, DateTimeOffset CreatedAt);
public sealed record CreateKbRequest(string Title, string Content, string Category, string Tags, string ResolutionSteps, bool IsPublished);
