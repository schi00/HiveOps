using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using HiveOps.Api.Authentication;
using HiveOps.Application.Interfaces;
using HiveOps.Domain.Entities;
using HiveOps.Infrastructure.Multitenancy;

namespace HiveOps.Api.Controllers;

/// <summary>
/// Self-improvement queue endpoints: exposes the per-tenant
/// <see cref="TenantImprovementSuggestion"/> queue that auto-files every agent
/// failure for human review. Humans can list the actionable queue, trigger a
/// scan over recent failed incidents, ask the service to LLM-draft/refine the
/// artifact payload, convert a suggestion into a real (approval-gated)
/// <see cref="TenantPlaybook"/>, or dismiss/reopen entries.
/// Tenant resolution mirrors <see cref="TenantContextPackController"/>: tenant
/// users resolve their own tenant from claims; privileged dashboard users
/// (Admin / SuperAdmin) may target a tenant explicitly via <c>?tenantId=</c>
/// or the <c>X-Tenant-Id</c> header.
/// </summary>
[ApiController]
[Route("api/support/improvements")]
[Authorize]
public sealed class ImprovementsController : ControllerBase
{
    private readonly ITenantImprovementService _improvements;
    private readonly TenantContext _tenantContext;
    private readonly ILogger<ImprovementsController> _logger;

    public ImprovementsController(
        ITenantImprovementService improvements,
        TenantContext tenantContext,
        ILogger<ImprovementsController> logger)
    {
        _improvements = improvements;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    private bool IsSuperAdmin => User.IsInRole(AppRoles.SuperAdmin);
    private bool IsAdmin => User.IsInRole(AppRoles.Admin);
    private bool IsPrivileged => IsSuperAdmin || IsAdmin;

    private string? CurrentUser =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name;

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

    /// <summary>
    /// Resolves the tenant an operation targets. Privileged users may pass an
    /// explicit <c>?tenantId=</c>; tenant-scoped callers resolve from their
    /// claim and are forbidden from pointing at another tenant.
    /// </summary>
    private bool TryResolveTenantId(
        Guid? requestedTenantId, out Guid resolvedTenantId, out ActionResult? error)
    {
        resolvedTenantId = Guid.Empty;
        error = null;

        if (IsPrivileged)
        {
            if (requestedTenantId.HasValue && requestedTenantId.Value != Guid.Empty)
            {
                resolvedTenantId = requestedTenantId.Value;
                if (!_tenantContext.IsResolved)
                    _tenantContext.SetTenant(resolvedTenantId);
                return true;
            }

            var effective = GetEffectiveTenantId();
            if (effective.HasValue)
            {
                resolvedTenantId = effective.Value;
                return true;
            }

            error = BadRequest(new { error = "Privileged requests must target a tenant (?tenantId= or X-Tenant-Id)." });
            return false;
        }

        var tenantClaim = User.FindFirstValue(AppClaimTypes.TenantId);
        if (!Guid.TryParse(tenantClaim, out resolvedTenantId))
        {
            error = Unauthorized(new { error = "Tenant context not available in user claims." });
            return false;
        }

        if (requestedTenantId.HasValue && requestedTenantId.Value != resolvedTenantId)
        {
            resolvedTenantId = Guid.Empty;
            error = Forbid();
            return false;
        }

        if (!_tenantContext.IsResolved)
            _tenantContext.SetTenant(resolvedTenantId);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  QUEUE
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lists suggestions newest-first. Optional <c>?status=</c> filter in
    /// {Suggested, Dismissed, Converted}; when omitted the service returns the
    /// actionable queue (Suggested + Converted).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TenantImprovementSuggestion>>> List(
        [FromQuery] string? status,
        [FromQuery] Guid? tenantId,
        CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var error)) return error!;

        if (status is not null
            && status is not ("Suggested" or "Dismissed" or "Converted"))
        {
            return BadRequest(new { error = "Invalid status. Valid values: Suggested, Dismissed, Converted." });
        }

        var suggestions = await _improvements.ListAsync(tid, status, ct);
        return Ok(suggestions);
    }

    /// <summary>
    /// Scans the last <c>takeLast</c> failed/escalated incidents and files
    /// suggestions for those lacking one (idempotent per incident).
    /// </summary>
    [HttpPost("scan")]
    public async Task<ActionResult> Scan(
        [FromQuery] Guid? tenantId,
        [FromQuery] int takeLast = 50,
        CancellationToken ct = default)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var error)) return error!;

        if (takeLast < 1)
            return BadRequest(new { error = "takeLast must be a positive integer." });

        var created = await _improvements.GenerateFromFailuresAsync(tid, takeLast, ct);

        _logger.LogInformation(
            "Tenant {TenantId} | improvement scan created {Created} suggestion(s) (takeLast={TakeLast}).",
            tid, created, takeLast);

        return Ok(new { created });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SUGGESTION LIFECYCLE
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>LLM-generates/refines the DraftPayloadJson for a Suggested entry.</summary>
    [HttpPost("{id:guid}/draft")]
    public async Task<ActionResult<TenantImprovementSuggestion>> Draft(
        Guid id, [FromQuery] Guid? tenantId, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var error)) return error!;

        var suggestion = await _improvements.GenerateDraftAsync(tid, id, ct);
        if (suggestion is null)
            return NotFound(new { error = "Suggestion not found or not in 'Suggested' status." });

        _logger.LogInformation("Tenant {TenantId} | improvement {SuggestionId} draft generated.", tid, id);
        return Ok(suggestion);
    }

    /// <summary>
    /// Converts a Suggested entry into a real TenantPlaybook
    /// (RequiresApproval=true, inactive until human review).
    /// </summary>
    [HttpPost("{id:guid}/accept")]
    public async Task<ActionResult<ConvertedPlaybookResponse>> Accept(
        Guid id, [FromQuery] Guid? tenantId, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var error)) return error!;

        var playbook = await _improvements.ConvertToPlaybookAsync(tid, id, CurrentUser, ct);
        if (playbook is null)
        {
            return BadRequest(new
            {
                error = "No convertible: generá el borrador primero o verificá que sea un playbook (NewPlaybook/DataQualityRule)."
            });
        }

        _logger.LogInformation(
            "Tenant {TenantId} | improvement {SuggestionId} converted to playbook {PlaybookId} ({Code}).",
            tid, id, playbook.Id, playbook.Code);

        return Ok(new ConvertedPlaybookResponse(
            playbook.Id, playbook.Code, playbook.Name, playbook.RequiresApproval));
    }

    /// <summary>Marks a suggestion as Dismissed.</summary>
    [HttpPost("{id:guid}/dismiss")]
    public async Task<ActionResult<TenantImprovementSuggestion>> Dismiss(
        Guid id, [FromQuery] Guid? tenantId, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var error)) return error!;

        var suggestion = await _improvements.SetStatusAsync(tid, id, "Dismissed", CurrentUser, ct);
        if (suggestion is null)
            return NotFound(new { error = "Suggestion not found or status cannot be changed." });

        _logger.LogInformation("Tenant {TenantId} | improvement {SuggestionId} dismissed.", tid, id);
        return Ok(suggestion);
    }

    /// <summary>Moves a Dismissed suggestion back to Suggested.</summary>
    [HttpPost("{id:guid}/reopen")]
    public async Task<ActionResult<TenantImprovementSuggestion>> Reopen(
        Guid id, [FromQuery] Guid? tenantId, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var error)) return error!;

        var suggestion = await _improvements.SetStatusAsync(tid, id, "Suggested", CurrentUser, ct);
        if (suggestion is null)
            return NotFound(new { error = "Suggestion not found or status cannot be changed." });

        _logger.LogInformation("Tenant {TenantId} | improvement {SuggestionId} reopened.", tid, id);
        return Ok(suggestion);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  DTOs
// ═══════════════════════════════════════════════════════════════════════════

public sealed record ConvertedPlaybookResponse(
    Guid PlaybookId,
    string Code,
    string Name,
    bool RequiresApproval);
