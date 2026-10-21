using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HiveOps.Api.Authentication;
using HiveOps.Application;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Api.Controllers;

/// <summary>
/// CRUD + dry-run endpoints for tenant operational playbooks (deterministic
/// L1/L2 runbooks executed by <see cref="IPlaybookRunner"/>).
/// Tenant resolution mirrors <see cref="SupportDashboardController"/>.
/// </summary>
[ApiController]
[Route("api/support/playbooks")]
[Authorize]
public sealed class PlaybooksController : ControllerBase
{
    private static readonly Regex PlaybookCodePattern = new(
        @"^[a-z0-9_\-]+$",
        RegexOptions.Compiled);

    private static readonly string[] AllowedActionTypes =
        ["SqlTemplate", "HttpWebhook", "StoredProcedure"];

    private readonly AppDbContext _db;
    private readonly TenantContext _tenantContext;
    private readonly IPlaybookRunner _playbookRunner;
    private readonly ILogger<PlaybooksController> _logger;

    public PlaybooksController(
        AppDbContext db,
        TenantContext tenantContext,
        IPlaybookRunner playbookRunner,
        ILogger<PlaybooksController> logger)
    {
        _db = db;
        _tenantContext = tenantContext;
        _playbookRunner = playbookRunner;
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

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PlaybookListItemDto>>> GetPlaybooks(CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue && !IsPrivileged) return Unauthorized();

        var query = IsPrivileged
            ? _db.TenantPlaybooks.IgnoreQueryFilters().AsNoTracking()
            : _db.TenantPlaybooks.AsNoTracking();
        if (effectiveTenantId.HasValue)
            query = query.Where(p => p.TenantId == effectiveTenantId.Value);

        var items = await query
            .OrderBy(p => p.Code)
            .Select(p => new PlaybookListItemDto(
                p.Id, p.Code, p.Name, p.ActionType, p.MaxAffectedRows, p.RequiresApproval, p.IsActive))
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost]
    public async Task<ActionResult<PlaybookListItemDto>> CreatePlaybook(
        [FromBody] CreatePlaybookRequest request, CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Code) || !PlaybookCodePattern.IsMatch(request.Code))
            return BadRequest(new { message = "Code must be a lowercase machine code matching ^[a-z0-9_-]+$." });

        var actionType = NormalizeActionType(request.ActionType);
        if (actionType is null)
            return BadRequest(new { message = $"ActionType must be one of: {string.Join(", ", AllowedActionTypes)}." });

        if (string.IsNullOrWhiteSpace(request.ActionPayloadTemplate))
            return BadRequest(new { message = "ActionPayloadTemplate must not be empty." });

        var requiredParameters = request.RequiredParametersJson ?? "[]";
        if (!IsValidParameterArray(requiredParameters))
            return BadRequest(new { message = "RequiredParametersJson must be a JSON array of parameter names." });

        if (actionType == "SqlTemplate" && !SafetyValidator.IsSafeSql(request.ActionPayloadTemplate))
        {
            var reason = SafetyValidator.GetRejectionReason(request.ActionPayloadTemplate);
            return BadRequest(new { message = $"ActionPayloadTemplate rejected by safety validator: {reason}" });
        }

        if (request.MaxAffectedRows is < 0)
            return BadRequest(new { message = "MaxAffectedRows must be zero or greater." });

        var code = request.Code.Trim();
        var exists = await _db.TenantPlaybooks
            .AnyAsync(p => p.TenantId == effectiveTenantId.Value && p.Code == code, ct);
        if (exists)
            return Conflict(new { message = $"A playbook with code '{code}' already exists for this tenant." });

        var playbook = new TenantPlaybook
        {
            Id = Guid.NewGuid(),
            TenantId = effectiveTenantId.Value,
            Code = code,
            Name = request.Name?.Trim() ?? string.Empty,
            TriggerSymptoms = request.TriggerSymptoms?.Trim() ?? string.Empty,
            ActionType = actionType,
            ActionPayloadTemplate = request.ActionPayloadTemplate,
            RequiredParametersJson = requiredParameters,
            VerificationSqlQuery = string.IsNullOrWhiteSpace(request.VerificationSqlQuery) ? null : request.VerificationSqlQuery,
            MaxAffectedRows = request.MaxAffectedRows ?? 5,
            RequiresApproval = request.RequiresApproval ?? false,
            IsActive = true
        };

        _db.TenantPlaybooks.Add(playbook);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Tenant {TenantId} | playbook {Code} ({Id}) created.", effectiveTenantId.Value, playbook.Code, playbook.Id);

        return Ok(ToDto(playbook));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PlaybookListItemDto>> UpdatePlaybook(
        Guid id, [FromBody] UpdatePlaybookRequest request, CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue) return Unauthorized();

        var playbook = await _db.TenantPlaybooks
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == effectiveTenantId.Value, ct);
        if (playbook is null) return NotFound();

        if (request.ActionPayloadTemplate is not null)
        {
            if (string.IsNullOrWhiteSpace(request.ActionPayloadTemplate))
                return BadRequest(new { message = "ActionPayloadTemplate must not be empty." });

            if (string.Equals(playbook.ActionType, "SqlTemplate", StringComparison.OrdinalIgnoreCase)
                && !SafetyValidator.IsSafeSql(request.ActionPayloadTemplate))
            {
                var reason = SafetyValidator.GetRejectionReason(request.ActionPayloadTemplate);
                return BadRequest(new { message = $"ActionPayloadTemplate rejected by safety validator: {reason}" });
            }

            playbook.ActionPayloadTemplate = request.ActionPayloadTemplate;
        }

        if (request.RequiredParametersJson is not null)
        {
            if (!IsValidParameterArray(request.RequiredParametersJson))
                return BadRequest(new { message = "RequiredParametersJson must be a JSON array of parameter names." });
            playbook.RequiredParametersJson = request.RequiredParametersJson;
        }

        if (request.MaxAffectedRows is < 0)
            return BadRequest(new { message = "MaxAffectedRows must be zero or greater." });

        if (request.Name is not null) playbook.Name = request.Name.Trim();
        if (request.TriggerSymptoms is not null) playbook.TriggerSymptoms = request.TriggerSymptoms.Trim();
        if (request.MaxAffectedRows.HasValue) playbook.MaxAffectedRows = request.MaxAffectedRows.Value;
        if (request.RequiresApproval.HasValue) playbook.RequiresApproval = request.RequiresApproval.Value;
        if (request.VerificationSqlQuery is not null)
            playbook.VerificationSqlQuery = string.IsNullOrWhiteSpace(request.VerificationSqlQuery) ? null : request.VerificationSqlQuery;

        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(playbook));
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> DeletePlaybook(Guid id, CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue) return Unauthorized();

        var playbook = await _db.TenantPlaybooks
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == effectiveTenantId.Value, ct);
        if (playbook is null) return NotFound();

        playbook.IsActive = false;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Tenant {TenantId} | playbook {Code} ({Id}) deactivated.", effectiveTenantId.Value, playbook.Code, id);

        return NoContent();
    }

    /// <summary>
    /// Executes the playbook with caller-supplied parameters under forced
    /// RequiresApproval semantics: the action is validated, dry-run inside a
    /// transaction and rolled back — nothing is ever committed.
    /// </summary>
    [HttpPost("{id:guid}/dry-run")]
    public async Task<ActionResult<PlaybookExecutionResult>> DryRunPlaybook(
        Guid id, [FromBody] PlaybookDryRunRequest request, CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue) return Unauthorized();

        var playbook = await _db.TenantPlaybooks
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == effectiveTenantId.Value, ct);
        if (playbook is null) return NotFound();

        // Detached clone with RequiresApproval forced — the tracked entity is never modified.
        var dryRunCopy = new TenantPlaybook
        {
            Id = playbook.Id,
            TenantId = playbook.TenantId,
            Code = playbook.Code,
            Name = playbook.Name,
            TriggerSymptoms = playbook.TriggerSymptoms,
            ActionType = playbook.ActionType,
            ActionPayloadTemplate = playbook.ActionPayloadTemplate,
            RequiredParametersJson = playbook.RequiredParametersJson,
            VerificationSqlQuery = playbook.VerificationSqlQuery,
            MaxAffectedRows = playbook.MaxAffectedRows,
            RequiresApproval = true,
            IsActive = playbook.IsActive
        };

        // Synthetic incident: parameter extraction falls back to the caller-supplied args.
        var probe = new Incident
        {
            TenantId = playbook.TenantId,
            Title = $"Playbook dry-run: {playbook.Code}",
            Description = string.Empty
        };

        var result = await _playbookRunner.ExecuteAsync(
            dryRunCopy, probe, request?.Parameters ?? new Dictionary<string, string>(), ct);
        return Ok(result);
    }

    private static string? NormalizeActionType(string? actionType)
    {
        if (string.IsNullOrWhiteSpace(actionType)) return null;
        return AllowedActionTypes.FirstOrDefault(
            t => string.Equals(t, actionType, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsValidParameterArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                   && doc.RootElement.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static PlaybookListItemDto ToDto(TenantPlaybook p) =>
        new(p.Id, p.Code, p.Name, p.ActionType, p.MaxAffectedRows, p.RequiresApproval, p.IsActive);
}

// ═══════════════════════════════════════════════════════════════════════════
//  DTOs
// ═══════════════════════════════════════════════════════════════════════════

public sealed record PlaybookListItemDto(
    Guid Id,
    string Code,
    string Name,
    string ActionType,
    int MaxAffectedRows,
    bool RequiresApproval,
    bool IsActive);

public sealed record CreatePlaybookRequest(
    string Code,
    string? Name,
    string? TriggerSymptoms,
    string ActionType,
    string ActionPayloadTemplate,
    string? RequiredParametersJson,
    string? VerificationSqlQuery,
    int? MaxAffectedRows,
    bool? RequiresApproval);

public sealed record UpdatePlaybookRequest(
    string? Name,
    string? TriggerSymptoms,
    string? ActionPayloadTemplate,
    string? RequiredParametersJson,
    string? VerificationSqlQuery,
    int? MaxAffectedRows,
    bool? RequiresApproval);

public sealed record PlaybookDryRunRequest(Dictionary<string, string>? Parameters);
