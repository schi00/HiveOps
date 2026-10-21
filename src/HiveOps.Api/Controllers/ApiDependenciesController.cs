using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HiveOps.Api.Authentication;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Api.Controllers;

[ApiController]
[Route("api/api-dependencies")]
[Authorize]
public sealed class ApiDependenciesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TenantContext _tenantContext;

    public ApiDependenciesController(
        AppDbContext db,
        TenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
    }

    private bool IsPrivileged => User.IsInRole(AppRoles.SuperAdmin) || User.IsInRole(AppRoles.Admin);

    private Guid? GetEffectiveTenantId()
    {
        if (_tenantContext.IsResolved) return _tenantContext.TenantId;
        
        if (IsPrivileged && Request.Headers.TryGetValue("X-Tenant-Id", out var headerValue) && 
            Guid.TryParse(headerValue.ToString(), out var tenantId))
        {
            return tenantId;
        }
        
        return null;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApiDependencyDto>>> GetAll(
        [FromQuery] ApiProvider? provider = null,
        [FromQuery] ApiDependencyStatus? status = null,
        CancellationToken ct = default)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue) return Unauthorized();

        var query = IsPrivileged
            ? _db.ApiDependencies.IgnoreQueryFilters().AsNoTracking()
            : _db.ApiDependencies.AsNoTracking();

        query = query.Where(ad => ad.TenantId == effectiveTenantId.Value);

        if (provider.HasValue)
            query = query.Where(ad => ad.ProviderName == provider.Value);
        
        if (status.HasValue)
            query = query.Where(ad => ad.Status == status.Value);

        var items = await query
            .OrderByDescending(ad => ad.LastChecked)
            .Select(ad => new ApiDependencyDto(
                ad.Id,
                ad.ProviderName.ToString(),
                ad.EndpointUrl,
                ad.HttpMethod ?? "GET",
                ad.ExpectedSchemaHash,
                ad.LastChecked,
                ad.Status.ToString(),
                ad.Status == ApiDependencyStatus.Healthy,
                ad.LastResponseSchema))
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiDependencyDto>> GetById(Guid id, CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue) return Unauthorized();

        var query = IsPrivileged
            ? _db.ApiDependencies.IgnoreQueryFilters().AsNoTracking()
            : _db.ApiDependencies.AsNoTracking();

        var dependency = await query
            .Where(ad => ad.Id == id && ad.TenantId == effectiveTenantId.Value)
            .Select(ad => new ApiDependencyDto(
                ad.Id,
                ad.ProviderName.ToString(),
                ad.EndpointUrl,
                ad.HttpMethod ?? "GET",
                ad.ExpectedSchemaHash,
                ad.LastChecked,
                ad.Status.ToString(),
                ad.Status == ApiDependencyStatus.Healthy,
                ad.LastResponseSchema))
            .FirstOrDefaultAsync(ct);

        if (dependency == null) return NotFound();

        return Ok(dependency);
    }

    [HttpPost]
    public async Task<ActionResult<ApiDependencyDto>> Create(
        [FromBody] CreateApiDependencyRequest request,
        CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue) return Unauthorized();

        if (!Enum.TryParse<ApiProvider>(request.ProviderName, true, out var provider))
            return BadRequest(new { error = "Invalid provider name" });

        var dependency = new ApiDependency
        {
            Id = Guid.NewGuid(),
            TenantId = effectiveTenantId.Value,
            ProviderName = provider,
            EndpointUrl = request.EndpointUrl,
            HttpMethod = request.HttpMethod?.ToUpperInvariant() ?? "GET",
            ExpectedSchemaHash = request.ExpectedSchemaHash ?? string.Empty,
            Status = string.IsNullOrEmpty(request.ExpectedSchemaHash) 
                ? ApiDependencyStatus.Learning 
                : ApiDependencyStatus.Healthy,
            LastChecked = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.ApiDependencies.Add(dependency);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(
            nameof(GetById),
            new { id = dependency.Id },
            new ApiDependencyDto(
                dependency.Id,
                dependency.ProviderName.ToString(),
                dependency.EndpointUrl,
                dependency.HttpMethod ?? "GET",
                dependency.ExpectedSchemaHash,
                dependency.LastChecked,
                dependency.Status.ToString(),
                dependency.Status == ApiDependencyStatus.Healthy,
                dependency.LastResponseSchema));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiDependencyDto>> Update(
        Guid id,
        [FromBody] UpdateApiDependencyRequest request,
        CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue) return Unauthorized();

        var query = IsPrivileged
            ? _db.ApiDependencies.IgnoreQueryFilters()
            : _db.ApiDependencies;

        var dependency = await query
            .FirstOrDefaultAsync(ad => ad.Id == id && ad.TenantId == effectiveTenantId.Value, ct);

        if (dependency == null) return NotFound();

        if (!string.IsNullOrEmpty(request.ProviderName) && 
            Enum.TryParse<ApiProvider>(request.ProviderName, true, out var provider))
        {
            dependency.ProviderName = provider;
        }

        if (!string.IsNullOrEmpty(request.EndpointUrl))
            dependency.EndpointUrl = request.EndpointUrl;

        if (!string.IsNullOrEmpty(request.HttpMethod))
            dependency.HttpMethod = request.HttpMethod.ToUpperInvariant();

        if (!string.IsNullOrEmpty(request.ExpectedSchemaHash))
        {
            dependency.ExpectedSchemaHash = request.ExpectedSchemaHash;
            dependency.Status = ApiDependencyStatus.Healthy;
        }

        dependency.LastChecked = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new ApiDependencyDto(
            dependency.Id,
            dependency.ProviderName.ToString(),
            dependency.EndpointUrl,
            dependency.HttpMethod ?? "GET",
            dependency.ExpectedSchemaHash,
            dependency.LastChecked,
            dependency.Status.ToString(),
            dependency.Status == ApiDependencyStatus.Healthy,
            dependency.LastResponseSchema));
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue) return Unauthorized();

        var query = IsPrivileged
            ? _db.ApiDependencies.IgnoreQueryFilters()
            : _db.ApiDependencies;

        var dependency = await query
            .FirstOrDefaultAsync(ad => ad.Id == id && ad.TenantId == effectiveTenantId.Value, ct);

        if (dependency == null) return NotFound();

        _db.ApiDependencies.Remove(dependency);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpPost("{id:guid}/recheck")]
    public async Task<ActionResult> ForceRecheck(Guid id, CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue) return Unauthorized();

        var query = IsPrivileged
            ? _db.ApiDependencies.IgnoreQueryFilters()
            : _db.ApiDependencies;

        var dependency = await query
            .FirstOrDefaultAsync(ad => ad.Id == id && ad.TenantId == effectiveTenantId.Value, ct);

        if (dependency == null) return NotFound();

        dependency.LastChecked = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new { message = "Recheck scheduled. The next API call to this endpoint will validate the schema." });
    }
}

// DTOs
public sealed record ApiDependencyDto(
    Guid Id,
    string ProviderName,
    string EndpointUrl,
    string HttpMethod,
    string ExpectedSchemaHash,
    DateTimeOffset LastChecked,
    string Status,
    bool IsHealthy,
    string? LastResponseSchema);

public sealed class CreateApiDependencyRequest
{
    public string ProviderName { get; set; } = string.Empty;
    public string EndpointUrl { get; set; } = string.Empty;
    public string? HttpMethod { get; set; }
    public string? ExpectedSchemaHash { get; set; }
}

public sealed class UpdateApiDependencyRequest
{
    public string? ProviderName { get; set; }
    public string? EndpointUrl { get; set; }
    public string? HttpMethod { get; set; }
    public string? ExpectedSchemaHash { get; set; }
}
