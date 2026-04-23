using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HiveOps.Agents.Support;
using HiveOps.Api.Authentication;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

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

    public SupportDashboardController(
        AppDbContext db,
        TenantContext tenantContext,
        IncidentAnalysisEngine analysisEngine,
        IGitService gitService,
        IDeploymentService deploymentService)
    {
        _db = db;
        _tenantContext = tenantContext;
        _analysisEngine = analysisEngine;
        _gitService = gitService;
        _deploymentService = deploymentService;
    }

    private bool IsSuperAdmin => User.IsInRole(AppRoles.SuperAdmin);
    private Guid EffectiveTenantId => IsSuperAdmin && _tenantContext.IsResolved
        ? _tenantContext.TenantId
        : _tenantContext.TenantId;

    // ═══════════════════════════════════════════════════════════════════════
    //  INCIDENTS
    // ═══════════════════════════════════════════════════════════════════════

    [HttpGet("summary")]
    public async Task<ActionResult<SupportSummaryDto>> GetSummary(CancellationToken ct)
    {
        if (!_tenantContext.IsResolved && !IsSuperAdmin) return Unauthorized();

        var tenantFilter = IsSuperAdmin ? (Guid?)null : _tenantContext.TenantId;

        var query = _db.Incidents.AsNoTracking();
        if (tenantFilter.HasValue)
            query = query.Where(i => i.TenantId == tenantFilter.Value);

        var totalOpen = await query.CountAsync(i => i.Status != IncidentStatus.Closed && i.Status != IncidentStatus.Resolved, ct);
        var totalResolved = await query.CountAsync(i => i.Status == IncidentStatus.Resolved, ct);
        var totalCritical = await query.CountAsync(i => i.Severity == IncidentSeverity.Critical && i.Status != IncidentStatus.Closed && i.Status != IncidentStatus.Resolved, ct);

        var avgResolution = await query
            .Where(i => i.Status == IncidentStatus.Resolved && i.ResolvedAt.HasValue)
            .Select(i => (double?)EF.Functions.DateDiffMinute(i.CreatedAt, i.ResolvedAt!.Value))
            .AverageAsync(ct);

        return Ok(new SupportSummaryDto(totalOpen, totalResolved, totalCritical, (int)(avgResolution ?? 0.0)));
    }

    [HttpGet("incidents")]
    public async Task<ActionResult<IReadOnlyList<IncidentListItemDto>>> GetIncidents(
        [FromQuery] string? status = null,
        [FromQuery] string? severity = null,
        [FromQuery] string? category = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (!_tenantContext.IsResolved && !IsSuperAdmin) return Unauthorized();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        var query = _db.Incidents.AsNoTracking();
        if (!IsSuperAdmin)
            query = query.Where(i => i.TenantId == _tenantContext.TenantId);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<IncidentStatus>(status, true, out var s))
            query = query.Where(i => i.Status == s);
        if (!string.IsNullOrWhiteSpace(severity) && Enum.TryParse<IncidentSeverity>(severity, true, out var sev))
            query = query.Where(i => i.Severity == sev);
        if (!string.IsNullOrWhiteSpace(category) && Enum.TryParse<IncidentCategory>(category, true, out var c))
            query = query.Where(i => i.Category == c);

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
                i.UpdatedAt))
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("incidents")]
    public async Task<ActionResult<IncidentDetailDto>> CreateIncident(
        [FromBody] CreateIncidentRequest request,
        CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantContext.TenantId,
            ConversationId = request.ConversationId,
            Title = request.Title,
            Description = request.Description,
            Category = request.Category ?? IncidentCategory.Other,
            Severity = request.Severity ?? IncidentSeverity.Low,
            Status = IncidentStatus.Open,
            AssignedTo = "bot",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.Incidents.Add(incident);
        await _db.SaveChangesAsync(ct);

        // LLM triage in background
        var analysis = await _analysisEngine.AnalyzeAsync(
            _tenantContext.TenantId, request.Description, incident.Id, ct);

        // Update incident with LLM classification if valid
        if (Enum.TryParse<IncidentCategory>(analysis.Category, true, out var cat))
            incident.Category = cat;
        if (Enum.TryParse<IncidentSeverity>(analysis.Severity, true, out var sev))
            incident.Severity = sev;

        incident.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(MapToDetail(incident, analysis));
    }

    [HttpGet("incidents/{id:guid}")]
    public async Task<ActionResult<IncidentDetailDto>> GetIncident(Guid id, CancellationToken ct)
    {
        if (!_tenantContext.IsResolved && !IsSuperAdmin) return Unauthorized();

        var query = _db.Incidents.AsNoTracking().Where(i => i.Id == id);
        if (!IsSuperAdmin)
            query = query.Where(i => i.TenantId == _tenantContext.TenantId);

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
            attachments));
    }

    [HttpPost("incidents/{id:guid}/attachments")]
    public async Task<ActionResult> AddAttachment(Guid id, [FromBody] AddAttachmentRequest request, CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        var incident = await _db.Incidents.FirstOrDefaultAsync(i => i.Id == id && i.TenantId == _tenantContext.TenantId, ct);
        if (incident is null) return NotFound();

        _db.IncidentAttachments.Add(new IncidentAttachment
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantContext.TenantId,
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
        if (!_tenantContext.IsResolved) return Unauthorized();

        var incident = await _db.Incidents
            .Include(i => i.Attachments)
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == _tenantContext.TenantId, ct);
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

        // Execute in a transaction (actual execution would need raw ADO.NET or EF migration)
        // For now, we record approval and mark as in-progress; real execution deferred to pipeline
        incident.Status = IncidentStatus.InProgress;
        incident.ResolutionNotes = (incident.ResolutionNotes ?? "") + $"\nDB fix approved and queued: {scriptAttachment.FileName}";
        incident.UpdatedAt = DateTimeOffset.UtcNow;

        _db.DiagnosticLogs.Add(new DiagnosticLog
        {
            TenantId = _tenantContext.TenantId,
            IncidentId = id,
            StepName = "db_fix_approved",
            Result = scriptAttachment.Content,
            IsSuccess = true
        });

        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "DB fix approved and queued for execution." });
    }

    [HttpPost("incidents/{id:guid}/reject-fix")]
    public async Task<ActionResult> RejectFix(Guid id, [FromBody] RejectFixRequest request, CancellationToken ct)
    {
        if (!_tenantContext.IsResolved) return Unauthorized();

        var incident = await _db.Incidents
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == _tenantContext.TenantId, ct);
        if (incident is null) return NotFound();

        incident.Status = IncidentStatus.Open;
        incident.ResolutionNotes = (incident.ResolutionNotes ?? "") + $"\nFix rejected: {request.Reason}";
        incident.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "Fix rejected. Incident reopened." });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MERGE QUEUE (SuperAdmin only)
    // ═══════════════════════════════════════════════════════════════════════

    [HttpGet("merge-queue")]
    [Authorize(Roles = AppRoles.SuperAdmin)]
    public async Task<ActionResult<IReadOnlyList<MergeRequestDto>>> GetMergeQueue(CancellationToken ct)
    {
        var prs = await _db.Incidents.AsNoTracking()
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
    [Authorize(Roles = AppRoles.SuperAdmin)]
    public async Task<ActionResult> ApproveMerge(Guid incidentId, CancellationToken ct)
    {
        var incident = await _db.Incidents.FindAsync(new object[] { incidentId }, ct);
        if (incident is null) return NotFound();

        if (string.IsNullOrWhiteSpace(incident.GitBranch))
            return BadRequest(new { message = "No branch associated with this incident." });

        // Merge PR via Git provider API
        var mergeResult = await _gitService.MergePullRequestAsync(incident.GitBranch, ct);

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

        return Ok(new { message = "Merged and deployed.", deployId });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SYSTEM HEALTH (SuperAdmin only)
    // ═══════════════════════════════════════════════════════════════════════

    [HttpGet("system-health")]
    [Authorize(Roles = AppRoles.SuperAdmin)]
    public async Task<ActionResult<SystemHealthDto>> GetSystemHealth(CancellationToken ct)
    {
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
        if (!_tenantContext.IsResolved) return Unauthorized();
        take = Math.Clamp(take, 1, 100);

        var query = _db.KbArticles.AsNoTracking()
            .Where(a => a.TenantId == _tenantContext.TenantId && a.IsPublished);

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
        if (!_tenantContext.IsResolved) return Unauthorized();

        var article = new KbArticle
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantContext.TenantId,
            Title = request.Title,
            Content = request.Content,
            Category = request.Category,
            Tags = request.Tags,
            ResolutionSteps = request.ResolutionSteps,
            IsPublished = request.IsPublished,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.KbArticles.Add(article);
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = article.Id });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════════

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
            analysis is null ? null : analysis.Reasoning);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  DTOs
// ═══════════════════════════════════════════════════════════════════════════

public sealed record SupportSummaryDto(int OpenIncidents, int ResolvedThisMonth, int CriticalOpen, int AvgResolutionMinutes);
public sealed record IncidentListItemDto(Guid Id, string Title, string Category, string Severity, string Status, string AssignedTo, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
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
    string? LlmReasoning = null);

public sealed record MergeRequestDto(Guid IncidentId, string Title, string Branch, string CommitHash, Guid TenantId, DateTimeOffset CreatedAt);
public sealed record SystemHealthDto(bool DbHealthy, bool PipelineHealthy, string LastTestRun);
public sealed record KbArticleDto(Guid Id, string Title, string Category, string Tags, DateTimeOffset CreatedAt);

public sealed record CreateIncidentRequest(Guid ConversationId, string Title, string Description, IncidentCategory? Category, IncidentSeverity? Severity);
public sealed record AddAttachmentRequest(IncidentAttachmentType Type, string FileName, string Content);
public sealed record RejectFixRequest(string Reason);
public sealed record CreateKbRequest(string Title, string Content, string Category, string Tags, string ResolutionSteps, bool IsPublished);
