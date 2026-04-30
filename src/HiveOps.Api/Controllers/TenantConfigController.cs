using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using HiveOps.Api.Authentication;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Models;

namespace HiveOps.Api.Controllers;

[ApiController]
[Route("api/tenant-config")]
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Tenant)]
public sealed class TenantConfigController : ControllerBase
{
    private readonly ITenantConfigService _tenantConfigService;

    public TenantConfigController(ITenantConfigService tenantConfigService)
    {
        _tenantConfigService = tenantConfigService;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] Guid? tenantId, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var resolvedTenantId, out var error))
            return error!;

        var config = await _tenantConfigService.GetConfigurationAsync(resolvedTenantId, ct);
        return Ok(config);
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromQuery] Guid? tenantId, [FromBody] TenantConfiguration config, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var resolvedTenantId, out var error))
            return error!;

        var errors = TenantConfigurationValidator.Validate(config);
        if (errors.Count > 0)
            return BadRequest(new { errors });

        var updated = await _tenantConfigService.UpsertConfigurationAsync(resolvedTenantId, config, Audit(nameof(TenantConfiguration)), ct);
        return Ok(updated);
    }

    [HttpPatch("agent")]
    public async Task<IActionResult> PatchAgent([FromQuery] Guid? tenantId, [FromBody] AgentConfig patch, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var resolvedTenantId, out var error))
            return error!;

        var current = await _tenantConfigService.GetConfigurationAsync(resolvedTenantId, ct);
        current.Agent = patch;
        var errors = TenantConfigurationValidator.Validate(current);
        if (errors.Count > 0)
            return BadRequest(new { errors });

        var updated = await _tenantConfigService.PatchAgentConfigAsync(resolvedTenantId, patch, Audit(nameof(AgentConfig)), ct);
        return Ok(updated);
    }

    [HttpPatch("tools")]
    public async Task<IActionResult> PatchTools([FromQuery] Guid? tenantId, [FromBody] ToolConfig patch, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var resolvedTenantId, out var error))
            return error!;

        var current = await _tenantConfigService.GetConfigurationAsync(resolvedTenantId, ct);
        current.Tools = patch;
        var errors = TenantConfigurationValidator.Validate(current);
        if (errors.Count > 0)
            return BadRequest(new { errors });

        var updated = await _tenantConfigService.PatchToolConfigAsync(resolvedTenantId, patch, Audit(nameof(ToolConfig)), ct);
        return Ok(updated);
    }

    [HttpPatch("business")]
    public async Task<IActionResult> PatchBusiness([FromQuery] Guid? tenantId, [FromBody] BusinessConfig patch, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var resolvedTenantId, out var error))
            return error!;

        var current = await _tenantConfigService.GetConfigurationAsync(resolvedTenantId, ct);
        current.Business = patch;
        var errors = TenantConfigurationValidator.Validate(current);
        if (errors.Count > 0)
            return BadRequest(new { errors });

        var updated = await _tenantConfigService.PatchBusinessConfigAsync(resolvedTenantId, patch, Audit(nameof(BusinessConfig)), ct);
        return Ok(updated);
    }

    [HttpPatch("llm")]
    public async Task<IActionResult> PatchLlm([FromQuery] Guid? tenantId, [FromBody] LlmConfig patch, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var resolvedTenantId, out var error))
            return error!;
        var current = await _tenantConfigService.GetConfigurationAsync(resolvedTenantId, ct);
        current.Llm = patch;
        var errors = TenantConfigurationValidator.Validate(current);
        if (errors.Count > 0)
            return BadRequest(new { errors });
        var updated = await _tenantConfigService.PatchLlmConfigAsync(resolvedTenantId, patch, Audit(nameof(LlmConfig)), ct);
        return Ok(updated);
    }

    [HttpPatch("deploy-git")]
    public async Task<IActionResult> PatchDeployGit([FromQuery] Guid? tenantId, [FromBody] DeployGitConfig patch, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var resolvedTenantId, out var error))
            return error!;
        var current = await _tenantConfigService.GetConfigurationAsync(resolvedTenantId, ct);
        current.DeployGit = patch;
        var errors = TenantConfigurationValidator.Validate(current);
        if (errors.Count > 0)
            return BadRequest(new { errors });
        var updated = await _tenantConfigService.PatchDeployGitConfigAsync(resolvedTenantId, patch, Audit(nameof(DeployGitConfig)), ct);
        return Ok(updated);
    }

    [HttpPatch("policies")]
    public async Task<IActionResult> PatchPolicies([FromQuery] Guid? tenantId, [FromBody] List<PolicyRule> patch, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var resolvedTenantId, out var error))
            return error!;
        var current = await _tenantConfigService.GetConfigurationAsync(resolvedTenantId, ct);
        current.Policies = patch ?? [];
        var errors = TenantConfigurationValidator.Validate(current);
        if (errors.Count > 0)
            return BadRequest(new { errors });
        var updated = await _tenantConfigService.PatchPoliciesAsync(resolvedTenantId, patch ?? [], Audit("Policies"), ct);
        return Ok(updated);
    }

    [HttpPatch("channel")]
    public async Task<IActionResult> PatchChannel([FromQuery] Guid? tenantId, [FromBody] ChannelConfig patch, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var resolvedTenantId, out var error))
            return error!;
        var current = await _tenantConfigService.GetConfigurationAsync(resolvedTenantId, ct);
        current.Channel = patch;
        var errors = TenantConfigurationValidator.Validate(current);
        if (errors.Count > 0)
            return BadRequest(new { errors });
        var updated = await _tenantConfigService.PatchChannelConfigAsync(resolvedTenantId, patch, Audit(nameof(ChannelConfig)), ct);
        return Ok(updated);
    }

    [HttpPatch("escalation")]
    public async Task<IActionResult> PatchEscalation([FromQuery] Guid? tenantId, [FromBody] EscalationConfig patch, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var resolvedTenantId, out var error))
            return error!;
        var current = await _tenantConfigService.GetConfigurationAsync(resolvedTenantId, ct);
        current.Escalation = patch;
        var errors = TenantConfigurationValidator.Validate(current);
        if (errors.Count > 0)
            return BadRequest(new { errors });
        var updated = await _tenantConfigService.PatchEscalationConfigAsync(resolvedTenantId, patch, Audit(nameof(EscalationConfig)), ct);
        return Ok(updated);
    }

    private TenantConfigAuditInfo Audit(string section) =>
        new(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name, section);

    private bool TryResolveTenantId(Guid? requestedTenantId, out Guid resolvedTenantId, out IActionResult? error)
    {
        error = null;

        if (User.IsInRole(AppRoles.Admin))
        {
            if (requestedTenantId is null || requestedTenantId == Guid.Empty)
            {
                resolvedTenantId = Guid.Empty;
                error = BadRequest("Admin requests require tenantId query parameter.");
                return false;
            }

            resolvedTenantId = requestedTenantId.Value;
            return true;
        }

        var tenantIdClaim = User.FindFirstValue(AppClaimTypes.TenantId);
        if (!Guid.TryParse(tenantIdClaim, out resolvedTenantId))
        {
            error = Unauthorized("Tenant context not available in user claims.");
            return false;
        }

        if (requestedTenantId.HasValue && requestedTenantId != resolvedTenantId)
        {
            error = Forbid();
            return false;
        }

        return true;
    }
}
