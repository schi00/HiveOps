using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HiveOps.Api.Authentication;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
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

        var config = await _configService.UpsertConfigurationAsync(id, configDto, ct);
        return Ok(config);
    }
}

// DTOs
public sealed record TenantDto(Guid Id, string Name, string? Industry, bool IsActive);
