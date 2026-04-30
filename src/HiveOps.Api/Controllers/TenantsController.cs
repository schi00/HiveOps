using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HiveOps.Api.Authentication;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Models;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Api.Controllers;

[ApiController]
[Route("api/tenants")]
[Authorize]
public sealed class TenantsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TenantContext _tenantContext;
    private readonly ITenantConfigService _configService;

    public TenantsController(AppDbContext db, TenantContext tenantContext, ITenantConfigService configService)
    {
        _db = db;
        _tenantContext = tenantContext;
        _configService = configService;
    }

    private bool IsAdminOrSuperAdmin => User.IsInRole(AppRoles.SuperAdmin) || User.IsInRole(AppRoles.Admin);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TenantDto>>> GetTenants(CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && !_tenantContext.IsResolved)
            return Unauthorized();

        var query = _db.Tenants.AsNoTracking();
        
        if (!IsAdminOrSuperAdmin)
            query = query.Where(t => t.Id == _tenantContext.TenantId);

        var tenants = await query
            .Select(t => new TenantDto(t.Id, t.Name, t.Industry, t.IsActive))
            .ToListAsync(ct);

        return Ok(tenants);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TenantDto>> GetTenant(Guid id, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId)
            return Forbid();

        var tenant = await _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, ct);

        if (tenant is null)
            return NotFound();

        return Ok(new TenantDto(tenant.Id, tenant.Name, tenant.Industry, tenant.IsActive));
    }

    [HttpGet("{id:guid}/config")]
    public async Task<ActionResult<TenantConfiguration>> GetTenantConfig(Guid id, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId)
            return Forbid();

        var config = await _configService.GetConfigurationAsync(id, ct);
        return Ok(config);
    }

    [HttpPut("{id:guid}/config")]
    public async Task<ActionResult<TenantConfiguration>> UpdateTenantConfig(Guid id, [FromBody] TenantConfiguration configDto, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId)
            return Forbid();

        var config = await _configService.UpsertConfigurationAsync(id, configDto, Audit(nameof(TenantConfiguration)), ct);
        return Ok(config);
    }

    [HttpPatch("{id:guid}/config/agent")]
    public async Task<ActionResult<TenantConfiguration>> PatchTenantAgent(Guid id, [FromBody] AgentConfig body, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var cfg = await _configService.GetConfigurationAsync(id, ct);
        cfg.Agent = body;
        var errors = TenantConfigurationValidator.Validate(cfg);
        if (errors.Count > 0) return BadRequest(new { errors });
        return Ok(await _configService.PatchAgentConfigAsync(id, body, Audit(nameof(AgentConfig)), ct));
    }

    [HttpPatch("{id:guid}/config/llm")]
    public async Task<ActionResult<TenantConfiguration>> PatchTenantLlm(Guid id, [FromBody] LlmConfig body, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var cfg = await _configService.GetConfigurationAsync(id, ct);
        cfg.Llm = body;
        var errors = TenantConfigurationValidator.Validate(cfg);
        if (errors.Count > 0) return BadRequest(new { errors });
        return Ok(await _configService.PatchLlmConfigAsync(id, body, Audit(nameof(LlmConfig)), ct));
    }

    [HttpPatch("{id:guid}/config/deploy-git")]
    public async Task<ActionResult<TenantConfiguration>> PatchTenantDeployGit(Guid id, [FromBody] DeployGitConfig body, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var cfg = await _configService.GetConfigurationAsync(id, ct);
        cfg.DeployGit = body;
        var errors = TenantConfigurationValidator.Validate(cfg);
        if (errors.Count > 0) return BadRequest(new { errors });
        return Ok(await _configService.PatchDeployGitConfigAsync(id, body, Audit(nameof(DeployGitConfig)), ct));
    }

    [HttpPatch("{id:guid}/config/policies")]
    public async Task<ActionResult<TenantConfiguration>> PatchTenantPolicies(Guid id, [FromBody] List<PolicyRule> body, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var cfg = await _configService.GetConfigurationAsync(id, ct);
        cfg.Policies = body ?? [];
        var errors = TenantConfigurationValidator.Validate(cfg);
        if (errors.Count > 0) return BadRequest(new { errors });
        return Ok(await _configService.PatchPoliciesAsync(id, body ?? [], Audit(nameof(TenantConfiguration.Policies)), ct));
    }

    [HttpPatch("{id:guid}/config/channel")]
    public async Task<ActionResult<TenantConfiguration>> PatchTenantChannel(Guid id, [FromBody] ChannelConfig body, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var cfg = await _configService.GetConfigurationAsync(id, ct);
        cfg.Channel = body;
        var errors = TenantConfigurationValidator.Validate(cfg);
        if (errors.Count > 0) return BadRequest(new { errors });
        return Ok(await _configService.PatchChannelConfigAsync(id, body, Audit(nameof(ChannelConfig)), ct));
    }

    [HttpPatch("{id:guid}/config/escalation")]
    public async Task<ActionResult<TenantConfiguration>> PatchTenantEscalation(Guid id, [FromBody] EscalationConfig body, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var cfg = await _configService.GetConfigurationAsync(id, ct);
        cfg.Escalation = body;
        var errors = TenantConfigurationValidator.Validate(cfg);
        if (errors.Count > 0) return BadRequest(new { errors });
        return Ok(await _configService.PatchEscalationConfigAsync(id, body, Audit(nameof(EscalationConfig)), ct));
    }

    private TenantConfigAuditInfo Audit(string section) =>
        new(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name, section);
}

// DTOs
public sealed record TenantDto(Guid Id, string Name, string? Industry, bool IsActive);
