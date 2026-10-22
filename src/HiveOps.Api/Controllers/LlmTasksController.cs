using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using HiveOps.Api.Authentication;
using HiveOps.Application.Interfaces;
using HiveOps.Domain.Entities;
using HiveOps.Infrastructure.Multitenancy;

namespace HiveOps.Api.Controllers;

/// <summary>
/// LLM retry pool: pending/dead-lettered AI tasks queued while providers were
/// down (doc distillation, DB profiling, improvement drafts). Tenant resolution
/// mirrors <see cref="TenantContextPackController"/>.
/// </summary>
[ApiController]
[Route("api/support/llm-tasks")]
[Authorize]
public sealed class LlmTasksController : ControllerBase
{
    private readonly ILlmTaskQueue _queue;
    private readonly TenantContext _tenantContext;
    private readonly ILogger<LlmTasksController> _logger;

    public LlmTasksController(
        ILlmTaskQueue queue,
        TenantContext tenantContext,
        ILogger<LlmTasksController> logger)
    {
        _queue = queue;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    private bool IsPrivileged =>
        User.IsInRole(AppRoles.SuperAdmin) || User.IsInRole(AppRoles.Admin);

    private string? CurrentUser =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name;

    private Guid? ResolveTenantFromHeader()
    {
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

    private bool TryResolveTenantId(Guid? requestedTenantId, out Guid resolvedTenantId, out ActionResult? error)
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

    /// <summary>Lists queued AI tasks; ?status=Pending|Completed|DeadLetter|Cancelled (all when omitted).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LlmPendingTask>>> List(
        [FromQuery] Guid? tenantId,
        [FromQuery] string? status,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var err)) return err!;
        return Ok(await _queue.ListAsync(tid, status, take, ct));
    }

    /// <summary>Reopens a task for immediate retry (worker picks it up next sweep).</summary>
    [HttpPost("{id:guid}/retry")]
    public async Task<ActionResult> RetryNow(
        Guid id, [FromQuery] Guid? tenantId, CancellationToken ct = default)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var err)) return err!;
        var ok = await _queue.RetryNowAsync(tid, id, ct);
        if (!ok) return NotFound(new { error = "Task not found or cancelled." });
        _logger.LogInformation("Tenant {TenantId} | LLM task {TaskId} requeued by {User}.", tid, id, CurrentUser);
        return Ok(new { requeued = true });
    }

    /// <summary>Cancels a Pending task (human reviewed; work handled manually).</summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult> Cancel(
        Guid id, [FromQuery] Guid? tenantId, CancellationToken ct = default)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var err)) return err!;
        var ok = await _queue.CancelAsync(tid, id, ct);
        if (!ok) return NotFound(new { error = "Task not found or already finished." });
        _logger.LogInformation("Tenant {TenantId} | LLM task {TaskId} cancelled by {User}.", tid, id, CurrentUser);
        return Ok(new { cancelled = true });
    }
}
