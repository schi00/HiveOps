using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using HiveOps.Api.Authentication;
using HiveOps.Application.Interfaces;
using HiveOps.Domain.Entities;
using HiveOps.Infrastructure.Multitenancy;

namespace HiveOps.Api.Controllers;

/// <summary>
/// Tenant Context Pack endpoints: exposes the durable per-tenant knowledge pack
/// (business glossary, database profile, repo profiles, resolution memory),
/// document ingestion/distillation and manual section edits that feed
/// <see cref="ITenantContextPackService.GetPromptDigestAsync"/> prompt injection.
/// Tenant resolution mirrors <see cref="PlaybooksController"/>: tenant users
/// resolve their own tenant from claims; privileged dashboard users (Admin /
/// SuperAdmin) may target a tenant explicitly via <c>?tenantId=</c> or the
/// <c>X-Tenant-Id</c> header.
/// </summary>
[ApiController]
[Route("api/support/context-pack")]
[Authorize]
public sealed class TenantContextPackController : ControllerBase
{
    /// <summary>Max extracted UTF-8 text accepted by document ingestion (512 KB).</summary>
    private const int MaxDocumentTextBytes = 512 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ITenantContextPackService _contextPacks;
    private readonly TenantContext _tenantContext;
    private readonly ILogger<TenantContextPackController> _logger;

    public TenantContextPackController(
        ITenantContextPackService contextPacks,
        TenantContext tenantContext,
        ILogger<TenantContextPackController> logger)
    {
        _contextPacks = contextPacks;
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
    //  PACK
    // ═══════════════════════════════════════════════════════════════════════

    [HttpGet]
    public async Task<ActionResult<TenantContextPack>> GetPack(
        [FromQuery] Guid? tenantId, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var error)) return error!;

        var pack = await _contextPacks.GetAsync(tid, ct);
        return pack is null
            ? NotFound(new { error = "No context pack exists for this tenant yet." })
            : Ok(pack);
    }

    /// <summary>Rebuilds the database profile section via read-only schema introspection.</summary>
    [HttpPost("refresh-db")]
    public async Task<ActionResult<TenantContextPack>> RefreshDatabaseProfile(
        [FromQuery] Guid? tenantId, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var error)) return error!;

        var pack = await _contextPacks.RefreshDatabaseProfileAsync(tid, CurrentUser, ct);

        _logger.LogInformation("Tenant {TenantId} | context-pack database profile refreshed.", tid);
        return Ok(pack);
    }

    /// <summary>Rebuilds the resolution-memory ring buffer from recent resolved/failed incidents.</summary>
    [HttpPost("rebuild-memory")]
    public async Task<ActionResult<TenantContextPack>> RebuildResolutionMemory(
        [FromQuery] Guid? tenantId, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var error)) return error!;

        var pack = await _contextPacks.RebuildResolutionMemoryAsync(tid, updatedBy: CurrentUser, ct: ct);

        _logger.LogInformation("Tenant {TenantId} | context-pack resolution memory rebuilt.", tid);
        return Ok(pack);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DOCUMENTS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Ingests a text document into the pack (chunked + distilled into the glossary).
    /// Accepts either multipart form-data with a <c>file</c> field or a JSON body
    /// <c>{ fileName, contentType, textContent }</c>. Extracted text is capped at 512 KB.
    /// </summary>
    [HttpPost("documents")]
    [RequestSizeLimit(1_000_000)]
    public async Task<ActionResult<TenantDocument>> IngestDocument(
        [FromQuery] Guid? tenantId, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var error)) return error!;

        string fileName;
        string contentType;
        string textContent;

        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return BadRequest(new { error = "Multipart request must include a non-empty 'file' field." });

            fileName = file.FileName;
            contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "text/plain" : file.ContentType;

            await using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            textContent = await reader.ReadToEndAsync(ct);
        }
        else
        {
            IngestDocumentRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<IngestDocumentRequest>(
                    Request.Body, JsonOptions, ct);
            }
            catch (JsonException)
            {
                return BadRequest(new { error = "Body must be valid JSON: { fileName, contentType, textContent }." });
            }

            if (request is null || string.IsNullOrWhiteSpace(request.FileName))
                return BadRequest(new { error = "fileName is required." });

            fileName = request.FileName;
            contentType = string.IsNullOrWhiteSpace(request.ContentType) ? "text/plain" : request.ContentType;
            textContent = request.TextContent ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(textContent))
            return BadRequest(new { error = "Document text content is empty." });

        var textBytes = Encoding.UTF8.GetByteCount(textContent);
        if (textBytes > MaxDocumentTextBytes)
        {
            return StatusCode(
                StatusCodes.Status413PayloadTooLarge,
                new { error = $"Document exceeds the 512KB text limit ({textBytes} bytes)." });
        }

        var document = await _contextPacks.IngestDocumentAsync(
            tid, fileName, contentType, textContent, CurrentUser, ct);

        _logger.LogInformation(
            "Tenant {TenantId} | context-pack document {FileName} ({DocumentId}) ingested.",
            tid, fileName, document.Id);

        return Ok(document);
    }

    [HttpGet("documents")]
    public async Task<ActionResult<IReadOnlyList<TenantDocument>>> ListDocuments(
        [FromQuery] Guid? tenantId, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var error)) return error!;

        var documents = await _contextPacks.ListDocumentsAsync(tid, ct);
        return Ok(documents);
    }

    [HttpDelete("documents/{id:guid}")]
    public async Task<ActionResult> DeleteDocument(
        Guid id, [FromQuery] Guid? tenantId, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var error)) return error!;

        var deleted = await _contextPacks.DeleteDocumentAsync(tid, id, ct);
        if (!deleted) return NotFound(new { error = "Document not found." });

        _logger.LogInformation("Tenant {TenantId} | context-pack document {DocumentId} deleted.", tid, id);
        return NoContent();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SECTIONS / EXPORT
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Manual update of one pack section. Valid section names:
    /// <c>domainGlossary</c>, <c>databaseProfile</c>, <c>repoProfiles</c>, <c>resolutionMemory</c>.
    /// </summary>
    [HttpPut("sections/{section}")]
    public async Task<ActionResult<TenantContextPack>> UpdateSection(
        string section,
        [FromBody] UpdateContextPackSectionRequest request,
        [FromQuery] Guid? tenantId,
        CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var error)) return error!;

        if (request is null || string.IsNullOrWhiteSpace(request.Json))
            return BadRequest(new { error = "Body must contain a non-empty 'json' property." });

        var pack = await _contextPacks.UpdateSectionAsync(tid, section, request.Json, CurrentUser, ct);
        return pack is null
            ? BadRequest(new
            {
                error = $"Unknown section '{section}' or invalid JSON. Valid sections: domainGlossary, databaseProfile, repoProfiles, resolutionMemory."
            })
            : Ok(pack);
    }

    /// <summary>Exports the full pack rendered as markdown.</summary>
    [HttpGet("export")]
    public async Task<ActionResult> ExportMarkdown(
        [FromQuery] Guid? tenantId, CancellationToken ct)
    {
        if (!TryResolveTenantId(tenantId, out var tid, out var error)) return error!;

        var markdown = await _contextPacks.ExportMarkdownAsync(tid, ct);
        return File(Encoding.UTF8.GetBytes(markdown), "text/markdown", "context-pack.md");
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  DTOs
// ═══════════════════════════════════════════════════════════════════════════

public sealed record IngestDocumentRequest(
    string? FileName,
    string? ContentType,
    string? TextContent);

public sealed record UpdateContextPackSectionRequest(string? Json);
