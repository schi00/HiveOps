using Microsoft.AspNetCore.Mvc;
using SaaSBot.Infrastructure.Multitenancy;
using SaaSBot.Workers;

namespace SaaSBot.Api.Controllers;

/// <summary>Handles catalog upload and ingestion job scheduling for a tenant.</summary>
[ApiController]
[Route("api/tenants/{tenantId:guid}/catalog")]
public sealed class CatalogController : ControllerBase
{
    private readonly TenantContext _tenantContext;

    public CatalogController(TenantContext tenantContext)
    {
        _tenantContext = tenantContext;
    }

    /// <summary>Uploads a product catalog CSV file. Requires X-Api-Key header.</summary>
    [HttpPost("upload")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB limit
    public async Task<IActionResult> Upload(Guid tenantId, IFormFile file, CancellationToken ct)
    {
        if (!_tenantContext.IsResolved)
            return Unauthorized();

        if (tenantId != _tenantContext.TenantId)
            return Forbid();

        if (file is null || file.Length == 0)
            return BadRequest("No file uploaded.");

        var allowed = new[] { ".csv", ".txt" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();

        if (!allowed.Contains(ext))
            return BadRequest("Only CSV files are supported.");

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);

        var job = new IngestionJob(tenantId, file.FileName, ms.ToArray());
        await CatalogIngestionWorker.EnqueueAsync(job, ct);

        return Accepted(new { message = "Catalog upload queued for processing.", tenantId });
    }
}
