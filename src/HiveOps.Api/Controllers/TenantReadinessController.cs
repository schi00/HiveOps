using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using HiveOps.Api.Authentication;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Models;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Api.Controllers;

/// <summary>
/// "Onboarding doctor" for autonomous support: reports whether a tenant is
/// ready for L1/L2/L3 autonomous resolution (target DB reachable, git
/// workspace cloned, KB / playbooks / channels configured) and exposes the
/// outcome-metering summary used for outcome-based billing.
/// </summary>
[ApiController]
[Route("api/support/readiness")]
[Authorize]
public sealed class TenantReadinessController : ControllerBase
{
    private const int AuditChainVerificationLimit = 100;

    private readonly AppDbContext _db;
    private readonly TenantContext _tenantContext;
    private readonly IDynamicConnectionStringResolver _connectionStringResolver;
    private readonly IGitService _gitService;
    private readonly IOutcomeMeteringService _outcomeMetering;
    private readonly ILogger<TenantReadinessController> _logger;

    public TenantReadinessController(
        AppDbContext db,
        TenantContext tenantContext,
        IDynamicConnectionStringResolver connectionStringResolver,
        IGitService gitService,
        IOutcomeMeteringService outcomeMetering,
        ILogger<TenantReadinessController> logger)
    {
        _db = db;
        _tenantContext = tenantContext;
        _connectionStringResolver = connectionStringResolver;
        _gitService = gitService;
        _outcomeMetering = outcomeMetering;
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

    /// <summary>
    /// Runs the onboarding-doctor checks for the effective tenant. Every check
    /// is independent: a failing check never aborts the remaining ones.
    /// </summary>
    [HttpGet("check")]
    public async Task<IActionResult> Check(CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue)
        {
            return IsPrivileged
                ? BadRequest(new { message = "Tenant could not be resolved. Supply the X-Tenant-Id header." })
                : Unauthorized();
        }
        var tenantId = effectiveTenantId.Value;

        var checks = new List<ReadinessCheckResult>
        {
            await CheckTargetDatabaseAsync(tenantId, ct),
            await CheckGitWorkspaceAsync(tenantId, ct),
            await CheckKnowledgeBaseAsync(ct),
            await CheckPlaybooksAsync(ct),
            await CheckChannelsAsync(tenantId, ct)
        };

        var ready = checks.First(c => c.Name == "targetDatabase").Ok
                    && checks.First(c => c.Name == "gitWorkspace").Ok;

        return Ok(new ReadinessReport(tenantId, DateTimeOffset.UtcNow, checks, ready));
    }

    /// <summary>
    /// Returns the outcome-metering summary for the effective tenant plus the
    /// audit-chain integrity state (when the ledger service is available).
    /// </summary>
    [HttpGet("outcomes")]
    public async Task<IActionResult> GetOutcomes(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue)
        {
            return IsPrivileged
                ? BadRequest(new { message = "Tenant could not be resolved. Supply the X-Tenant-Id header." })
                : Unauthorized();
        }
        var tenantId = effectiveTenantId.Value;

        var effectiveTo = to ?? DateTimeOffset.UtcNow;
        var effectiveFrom = from ?? effectiveTo.AddDays(-30);

        var summary = await _outcomeMetering.GetSummaryAsync(tenantId, effectiveFrom, effectiveTo, ct);

        // The audit ledger is optional until its DI wiring lands; resolve it
        // lazily so this endpoint keeps working either way.
        bool? auditChainIntact = null;
        var ledger = HttpContext.RequestServices.GetService<IIncidentAuditLedger>();
        if (ledger is not null)
        {
            try
            {
                var auditedIncidentIds = await _db.IncidentAuditEntries
                    .Where(e => e.CreatedAt >= effectiveFrom && e.CreatedAt <= effectiveTo)
                    .Select(e => e.IncidentId)
                    .Distinct()
                    .Take(AuditChainVerificationLimit)
                    .ToListAsync(ct);

                var intact = true;
                foreach (var incidentId in auditedIncidentIds)
                {
                    var verification = await ledger.VerifyChainAsync(tenantId, incidentId, ct);
                    if (!verification.IsValid)
                    {
                        intact = false;
                        _logger.LogWarning(
                            "Audit chain verification failed for incident {IncidentId} (tenant {TenantId}) at entry {EntryId}.",
                            incidentId, tenantId, verification.BrokenAtEntryId);
                    }
                }
                auditChainIntact = intact;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audit chain verification unavailable for tenant {TenantId}.", tenantId);
            }
        }

        var operatingMode = TenantOperatingMode.Autonomous.ToString();
        var tenantConfigService = HttpContext.RequestServices.GetService<ITenantConfigService>();
        if (tenantConfigService is not null)
        {
            try
            {
                var config = await tenantConfigService.GetConfigurationAsync(tenantId, ct);
                if (config?.Agent is not null)
                {
                    operatingMode = config.Agent.OperatingMode.ToString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve operating mode for tenant {TenantId}; defaulting to Autonomous.", tenantId);
            }
        }

        return Ok(new OutcomeSummaryResponse(
            summary.TenantId,
            summary.From,
            summary.To,
            summary.TotalIncidents,
            summary.VerifiedAutonomousResolutions,
            summary.AssistedResolutions,
            summary.RollbacksCount,
            auditChainIntact,
            summary.ShadowProposedResolutions,
            summary.EstimatedHoursSaved,
            summary.EstimatedCostSavingsUsd,
            operatingMode));
    }

    /// <summary>
    /// Updates the commercial operating mode (Shadow, Assisted, or Autonomous)
    /// for the effective tenant.
    /// </summary>
    [HttpPut("operating-mode")]
    public async Task<IActionResult> UpdateOperatingMode(
        [FromBody] UpdateOperatingModeRequest? request,
        CancellationToken ct)
    {
        var effectiveTenantId = GetEffectiveTenantId();
        if (!effectiveTenantId.HasValue)
        {
            return IsPrivileged
                ? BadRequest(new { message = "Tenant could not be resolved. Supply the X-Tenant-Id header." })
                : Unauthorized();
        }
        var tenantId = effectiveTenantId.Value;

        if (request is null
            || string.IsNullOrWhiteSpace(request.OperatingMode)
            || !Enum.TryParse<TenantOperatingMode>(request.OperatingMode.Trim(), ignoreCase: true, out var parsedMode)
            || !Enum.IsDefined(typeof(TenantOperatingMode), parsedMode))
        {
            return BadRequest(new
            {
                message = "Invalid operatingMode. Allowed values: Shadow, Assisted, Autonomous."
            });
        }

        var tenantConfigService = HttpContext.RequestServices.GetService<ITenantConfigService>();
        if (tenantConfigService is not null)
        {
            var config = await tenantConfigService.GetConfigurationAsync(tenantId, ct);
            config.Agent.OperatingMode = parsedMode;
            var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name;
            await tenantConfigService.UpsertConfigurationAsync(
                tenantId,
                config,
                new TenantConfigAuditInfo(actor, nameof(AgentConfig)),
                ct);
        }

        return Ok(new { tenantId, operatingMode = parsedMode.ToString() });
    }

    /// <summary>
    /// Resolves the tenant connection string and probes the target database,
    /// counting the tables visible through INFORMATION_SCHEMA.
    /// </summary>
    private async Task<ReadinessCheckResult> CheckTargetDatabaseAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            await _connectionStringResolver.WarmCacheAsync(tenantId, ct);
            var connectionString = _connectionStringResolver.Resolve(tenantId);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables";
            cmd.CommandTimeout = 10;
            var tableCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));

            return new ReadinessCheckResult("targetDatabase", true, $"{tableCount} tables visible in the target database.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Readiness check 'targetDatabase' failed for tenant {TenantId}.", tenantId);
            return new ReadinessCheckResult("targetDatabase", false, ex.Message);
        }
    }

    /// <summary>
    /// Verifies the tenant git workspace exists on disk and is a valid clone
    /// (contains a .git directory).
    /// </summary>
    private async Task<ReadinessCheckResult> CheckGitWorkspaceAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            var path = await _gitService.GetWorkspacePathAsync(tenantId, ct);
            if (string.IsNullOrWhiteSpace(path))
                return new ReadinessCheckResult("gitWorkspace", false, "No workspace path resolved for the tenant.");

            var exists = Directory.Exists(path);
            var isGitRepo = exists && Directory.Exists(Path.Combine(path, ".git"));
            var ok = exists && isGitRepo;
            var detail = ok
                ? path
                : !exists
                    ? $"Workspace directory does not exist: {path}"
                    : $"Directory is not a git repository (missing .git): {path}";
            return new ReadinessCheckResult("gitWorkspace", ok, detail);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Readiness check 'gitWorkspace' failed for tenant {TenantId}.", tenantId);
            return new ReadinessCheckResult("gitWorkspace", false, ex.Message);
        }
    }

    /// <summary>Counts published knowledge-base articles for the tenant.</summary>
    private async Task<ReadinessCheckResult> CheckKnowledgeBaseAsync(CancellationToken ct)
    {
        try
        {
            var count = await _db.KbArticles.CountAsync(k => k.IsPublished, ct);
            return new ReadinessCheckResult(
                "knowledgeBase",
                count > 0,
                $"{count} published KB articles.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Readiness check 'knowledgeBase' failed.");
            return new ReadinessCheckResult("knowledgeBase", false, ex.Message);
        }
    }

    /// <summary>Counts active operational playbooks owned by the tenant.</summary>
    private async Task<ReadinessCheckResult> CheckPlaybooksAsync(CancellationToken ct)
    {
        try
        {
            var count = await _db.TenantPlaybooks.CountAsync(p => p.IsActive, ct);
            return new ReadinessCheckResult(
                "playbooks",
                count > 0,
                $"{count} active playbooks.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Readiness check 'playbooks' failed.");
            return new ReadinessCheckResult("playbooks", false, ex.Message);
        }
    }

    /// <summary>
    /// Reports whether at least one inbound channel (WhatsApp number or the
    /// ticketing channel) is configured for the tenant.
    /// </summary>
    private async Task<ReadinessCheckResult> CheckChannelsAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            var tenant = await _db.Tenants.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == tenantId, ct);
            if (tenant is null)
                return new ReadinessCheckResult("channels", false, "Tenant record not found.");

            var whatsappConfigured = !string.IsNullOrWhiteSpace(tenant.WhatsAppNumber);
            var ticketingConfigured = await IsTicketingConfiguredAsync(tenant, ct);
            var webhookSettings = HiveOps.Api.Utilities.TenantSettingsJson.Parse(tenant.ConfigJson).OutboundWebhook;
            var chatOpsWebhookConfigured = webhookSettings.Enabled && !string.IsNullOrWhiteSpace(webhookSettings.Url);

            var ok = whatsappConfigured || ticketingConfigured || chatOpsWebhookConfigured;
            var detail = ok
                ? $"WhatsApp={(whatsappConfigured ? "OK" : "—")} · Ticketing={(ticketingConfigured ? "OK" : "—")} · ChatOps/Webhook={(chatOpsWebhookConfigured ? "OK" : "—")}"
                : "Sin canales activos: configurá WhatsApp, Ticketing o el Webhook de Slack/Teams en Configuración.";
            return new ReadinessCheckResult("channels", ok, detail);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Readiness check 'channels' failed for tenant {TenantId}.", tenantId);
            return new ReadinessCheckResult("channels", false, ex.Message);
        }
    }

    /// <summary>
    /// Ticketing is considered configured when the tenant ConfigJson declares a
    /// truthy "ticketing" feature flag, or (fallback) the tenant already has
    /// conversations flowing through the ticketing channel.
    /// </summary>
    private async Task<bool> IsTicketingConfiguredAsync(Tenant tenant, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(tenant.ConfigJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(tenant.ConfigJson);
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "featureFlags", StringComparison.OrdinalIgnoreCase)
                        || property.Value.ValueKind != JsonValueKind.Object)
                        continue;

                    foreach (var flag in property.Value.EnumerateObject())
                    {
                        if (flag.Name.Contains("ticketing", StringComparison.OrdinalIgnoreCase)
                            && flag.Value.ValueKind == JsonValueKind.True)
                            return true;
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed ConfigJson — fall through to the usage signal.
            }
        }

        return await _db.Conversations.AnyAsync(c => c.Channel == "ticketing", ct);
    }

    /// <summary>Result of a single, independent readiness check.</summary>
    private sealed record ReadinessCheckResult(string Name, bool Ok, string Detail);

    /// <summary>Aggregated onboarding-doctor report for a tenant.</summary>
    private sealed record ReadinessReport(
        Guid TenantId,
        DateTimeOffset CheckedAt,
        IReadOnlyList<ReadinessCheckResult> Checks,
        bool ReadyForAutonomousSupport)
    {
        public bool Ready => ReadyForAutonomousSupport;
    }

    /// <summary>Request payload for updating the tenant operating mode.</summary>
    public sealed record UpdateOperatingModeRequest(string? OperatingMode);

    /// <summary>Tenant outcome summary plus audit-chain integrity state and ROI metrics.</summary>
    private sealed record OutcomeSummaryResponse(
        Guid TenantId,
        DateTimeOffset From,
        DateTimeOffset To,
        int TotalIncidents,
        int VerifiedAutonomousResolutions,
        int AssistedResolutions,
        int RollbacksCount,
        bool? AuditChainIntact,
        int ShadowProposedResolutions = 0,
        double EstimatedHoursSaved = 0,
        decimal EstimatedCostSavingsUsd = 0m,
        string OperatingMode = "Autonomous");
}
