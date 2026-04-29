using Microsoft.AspNetCore.Mvc;
using HiveOps.Infrastructure.Secrets;
using HiveOps.Api.Hubs;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;

namespace HiveOps.Api.Controllers;

[ApiController]
[Route("api/ci")] 
public sealed class CiController : ControllerBase
{
    private readonly ILogger<CiController> _logger;
    private readonly ISecretProvider _secrets;
    private readonly AppDbContext _db;
    private readonly TenantContext _tenantContext;
    private readonly IHubContext<SupervisionHub> _hub;

    public CiController(ILogger<CiController> logger, ISecretProvider secrets, AppDbContext db, TenantContext tenantContext, IHubContext<SupervisionHub> hub)
    {
        _logger = logger;
        _secrets = secrets;
        _db = db;
        _tenantContext = tenantContext;
        _hub = hub;
    }

    [HttpPost("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback()
    {
        var signature = Request.Headers["X-CI-Signature"].ToString();
        var tsHeader = Request.Headers["X-CI-Timestamp"].ToString();
        if (!long.TryParse(tsHeader, out var unix)) return Unauthorized();
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(unix);

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var body = ms.ToArray();

        _ = _secrets.TryGetSecret("Ci:HmacSecret", out var secret, out _);
        var ok = HmacValidator.IsValid(secret, body, timestamp, TimeSpan.FromMinutes(5), signature);
        if (!ok)
        {
            _logger.LogWarning("CI callback rejected: invalid HMAC or timestamp.");
            return Unauthorized();
        }

        // Parse minimal JSON payload
        var payload = System.Text.Json.JsonDocument.Parse(body).RootElement;
        var incidentId = payload.TryGetProperty("incidentId", out var iid) ? iid.GetGuid() : Guid.Empty;
        var status = payload.TryGetProperty("status", out var st) ? st.GetString() : null;
        var runId = payload.TryGetProperty("runId", out var rid) ? rid.GetString() : null;
        var logUrl = payload.TryGetProperty("logUrl", out var lu) ? lu.GetString() : null;
        var progress = payload.TryGetProperty("progress", out var pr) && pr.ValueKind==System.Text.Json.JsonValueKind.Number ? pr.GetInt32() : (int?)null;
        var etaSeconds = payload.TryGetProperty("etaSeconds", out var eta) && eta.ValueKind==System.Text.Json.JsonValueKind.Number ? eta.GetInt32() : (int?)null;
        var tenantId = payload.TryGetProperty("tenantId", out var tid) && tid.ValueKind==System.Text.Json.JsonValueKind.String ? (Guid.TryParse(tid.GetString(), out var tg) ? tg : Guid.Empty) : Guid.Empty;
        var action = payload.TryGetProperty("action", out var act) ? act.GetString() : null;

        if (incidentId == Guid.Empty || string.IsNullOrWhiteSpace(status))
        {
            _logger.LogWarning("CI callback missing required fields.");
            return BadRequest("Missing incidentId or status");
        }

        // Resolve tenant
        if (tenantId != Guid.Empty)
        {
            _tenantContext.SetTenant(tenantId);
        }
        else
        {
            // Lookup incident ignoring filters to find tenant
            var found = await _db.Incidents.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(i => i.Id == incidentId);
            if (found is null)
            {
                _logger.LogWarning("CI callback incident not found: {IncidentId}", incidentId);
                return NotFound();
            }
            _tenantContext.SetTenant(found.TenantId);
        }

        // Update incident status fields
        var inc = await _db.Incidents.FirstOrDefaultAsync(i => i.Id == incidentId);
        if (inc is null)
        {
            _logger.LogWarning("CI callback tenant-scoped incident not found: {IncidentId}", incidentId);
            return NotFound();
        }

        var combinedStatus = string.IsNullOrWhiteSpace(action) ? status : ($"{action}_{status}");
        inc.DeployStatus = combinedStatus;
        inc.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.DiagnosticLogs.AddAsync(new HiveOps.Domain.Entities.DiagnosticLog
        {
            TenantId = inc.TenantId,
            IncidentId = inc.Id,
            StepName = "ci.callback",
            Result = System.Text.Json.JsonSerializer.Serialize(new { runId, logUrl, action }),
            IsSuccess = (combinedStatus ?? string.Empty).Contains("SUCCESS", StringComparison.OrdinalIgnoreCase)
        });
        await _db.SaveChangesAsync();

        // Notify tenant group via SignalR
        var payloadOut = new { incidentId, status = combinedStatus, progress = progress ?? 0, eta = etaSeconds ?? 0, at = DateTimeOffset.UtcNow, runId, logUrl };
        await _hub.Clients.Group($"tenant:{inc.TenantId}").SendAsync("DeploymentUpdated", payloadOut);

        // Metered billing: record successful deployments
        try
        {
            if ((combinedStatus ?? string.Empty).Contains("SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                var stripe = HttpContext.RequestServices.GetService<HiveOps.Application.Interfaces.IStripeService>();
                if (stripe is not null)
                    await stripe.ReportUsageAsync(inc.TenantId, "deploy.success", 1, DateTimeOffset.UtcNow, runId, HttpContext.RequestAborted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stripe metered billing reporting failed for incident {IncidentId}", incidentId);
        }

        _logger.LogInformation("CI callback accepted. Incident={IncidentId} Status={Status}", incidentId, status);
        return Ok(new { accepted = true });
    }
}
