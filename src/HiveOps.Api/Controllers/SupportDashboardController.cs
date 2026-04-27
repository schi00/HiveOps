using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HiveOps.Agents.Support;
using HiveOps.Api.Authentication;
using HiveOps.Application;
using HiveOps.Application.Interfaces;
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
    private readonly ITenantDataFixerService _dataFixer;
    private readonly ISupervisionNotifier _notifier;

    public SupportDashboardController(
        AppDbContext db,
        TenantContext tenantContext,
        IncidentAnalysisEngine analysisEngine,
        IGitService gitService,
        IDeploymentService deploymentService,
        ITenantDataFixerService dataFixer,
        ISupervisionNotifier notifier)
    {
        _db = db;
        _tenantContext = tenantContext;
        _analysisEngine = analysisEngine;
        _gitService = gitService;
        _deploymentService = deploymentService;
        _dataFixer = dataFixer;
        _notifier = notifier;
    }

    private bool IsSuperAdmin => User.IsInRole(AppRoles.SuperAdmin);
    private bool IsAdmin => User.IsInRole(AppRoles.Admin);
    private bool IsPrivileged => IsSuperAdmin || IsAdmin;

    private Guid? ResolveTenantFromHeader()
    {
        if (Request.Headers.TryGetValue("X-Tenant-Id", out var headerValue) && Guid.TryParse(headerValue.ToString(), out var tenantId))
            return tenantId;
        return null;
    }

    private Guid? GetEffectiveTenantId()
    {
        if (_tenantContext.IsResolved) return _tenantContext.TenantId;
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
        var totalResolved = await query.CountAsync(i => i.Status == IncidentStatus.Resolved, ct);
        var totalCritical = await query.CountAsync(i => i.Severity == IncidentSeverity.Critical && i.Status != IncidentStatus.Closed && i.Status != IncidentStatus.Resolved, ct);

        var resolvedTimes = await query
            .Where(i => i.Status == IncidentStatus.Resolved && i.ResolvedAt.HasValue)
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
                i.UpdatedAt))
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

        // LLM triage — non-blocking; failure should not prevent incident creation
        IncidentAnalysisResult? analysis = null;
        try
        {
            analysis = await _analysisEngine.AnalyzeAsync(
                effectiveTenantId.Value, request.Description, incident.Id, ct);

            if (Enum.TryParse<IncidentCategory>(analysis.Category, true, out var cat))
                incident.Category = cat;
            if (Enum.TryParse<IncidentSeverity>(analysis.Severity, true, out var sev))
                incident.Severity = sev;

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
        return Ok(new { message = "Fix rejected. Incident reopened." });
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
        if (!_tenantContext.IsResolved && !IsPrivileged) return Unauthorized();
        take = Math.Clamp(take, 1, 100);

        var query = _db.KbArticles.AsNoTracking();
        
        // Privileged users see all KB articles, regular users see only their tenant's
        if (!IsPrivileged)
        {
            query = query.Where(a => a.TenantId == _tenantContext.TenantId);
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

                if (Enum.TryParse<IncidentCategory>(analysis.Category, true, out var cat))
                    incident.Category = cat;
                if (Enum.TryParse<IncidentSeverity>(analysis.Severity, true, out var sev))
                    incident.Severity = sev;

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
        // The TenantSessionContextInterceptor will automatically set SESSION_CONTEXT when DB operations occur
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

            if (Enum.TryParse<IncidentCategory>(analysis.Category, true, out var cat))
                incident.Category = cat;
            if (Enum.TryParse<IncidentSeverity>(analysis.Severity, true, out var sev))
                incident.Severity = sev;

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

public sealed record CreateIncidentRequest(Guid? ConversationId, string Title, string Description, string? Category, string? Severity);
public sealed record CreateIncidentFromSandpitRequest(string Title, string Description);
public sealed record AddAttachmentRequest(IncidentAttachmentType Type, string FileName, string Content);
public sealed record RejectFixRequest(string Reason);
public sealed record MessageDto(Guid Id, string Role, string Content, DateTimeOffset CreatedAt);
public sealed record CreateKbRequest(string Title, string Content, string Category, string Tags, string ResolutionSteps, bool IsPublished);
