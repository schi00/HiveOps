using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HiveOps.Api.Authentication;
using HiveOps.Api.Services;
using HiveOps.Api.Utilities;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Models;
using HiveOps.Application.Policies;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Services;

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

    // ── Tenant policy rules (NL → structured, enforced by TenantPolicyEvaluator) ──

    /// <summary>Interpret a natural-language rule without saving it — preview + overlap warnings.</summary>
    [HttpPost("{id:guid}/policies/interpret")]
    public async Task<ActionResult<PolicyInterpretationResult>> InterpretPolicyRule(
        Guid id,
        [FromBody] InterpretPolicyRequest body,
        [FromServices] IPolicyRuleInterpreter interpreter,
        CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var cfg = await _configService.GetConfigurationAsync(id, ct);
        var result = await interpreter.InterpretAsync(id, body?.Text ?? string.Empty, cfg.Policies, ct);
        return Ok(result);
    }

    /// <summary>
    /// Adds a policy rule. Accepts either a pre-interpreted <c>rule</c> (from /interpret)
    /// or raw <c>text</c> to interpret inline. Appends at the bottom (exceptions last)
    /// unless the interpreter flagged an insertion point and the client opts in.
    /// </summary>
    [HttpPost("{id:guid}/policies")]
    public async Task<ActionResult<TenantConfiguration>> AddPolicyRule(
        Guid id,
        [FromBody] AddPolicyRequest body,
        [FromServices] IPolicyRuleInterpreter interpreter,
        CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var cfg = await _configService.GetConfigurationAsync(id, ct);
        var rules = cfg.Policies ?? [];

        PolicyRule? rule = body?.Rule;
        var insertBefore = body?.InsertBeforeRuleId;
        if (rule is null)
        {
            var interpretation = await interpreter.InterpretAsync(id, body?.Text ?? string.Empty, rules, ct);
            if (!interpretation.Ok || interpretation.Rule is null)
                return BadRequest(new { errors = new[] { interpretation.Error ?? "No se pudo interpretar la regla." } });
            rule = interpretation.Rule;
            insertBefore ??= interpretation.SuggestedInsertBeforeRuleId;
        }

        if (string.IsNullOrWhiteSpace(rule.Action) || !PolicyActions.All.Contains(rule.Action))
            return BadRequest(new { errors = new[] { "La regla no tiene acción válida." } });

        // Closed vocabulary: strip parameter keys the evaluator doesn't understand.
        if (rule.Parameters is not null)
            rule.Parameters = new Dictionary<string, string>(
                rule.Parameters.Where(kv => PolicyConditionKeys.All.Contains(kv.Key)),
                StringComparer.OrdinalIgnoreCase);
        else
            rule.Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rule.Condition))
            rule.Condition = PolicyRuleInterpreterService.RenderCondition(rule);
        if (string.IsNullOrWhiteSpace(rule.Id))
            rule.Id = Guid.NewGuid().ToString("N")[..8];

        var insertAt = rules.Count;
        if (!string.IsNullOrWhiteSpace(insertBefore))
        {
            var idx = rules.FindIndex(r => r.Id == insertBefore);
            if (idx >= 0) insertAt = idx;
        }
        rules.Insert(insertAt, rule);

        cfg.Policies = rules;
        var errors = TenantConfigurationValidator.Validate(cfg);
        if (errors.Count > 0) return BadRequest(new { errors });
        return Ok(await _configService.PatchPoliciesAsync(id, rules, Audit(nameof(TenantConfiguration.Policies)), ct));
    }

    /// <summary>Reorder rules — list order is evaluation order (last match wins).</summary>
    [HttpPatch("{id:guid}/policies/reorder")]
    public async Task<ActionResult<TenantConfiguration>> ReorderPolicyRules(
        Guid id,
        [FromBody] ReorderPoliciesRequest body,
        CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var cfg = await _configService.GetConfigurationAsync(id, ct);
        var current = cfg.Policies ?? [];

        if (body?.RuleIds is null || body.RuleIds.Count != current.Count
            || body.RuleIds.Distinct().Count() != current.Count
            || body.RuleIds.Any(rid => current.All(r => r.Id != rid)))
            return BadRequest(new { errors = new[] { "ruleIds debe contener exactamente los ids actuales, sin duplicados." } });

        var reordered = body.RuleIds
            .Select(rid => current.First(r => r.Id == rid))
            .ToList();

        return Ok(await _configService.PatchPoliciesAsync(id, reordered, Audit(nameof(TenantConfiguration.Policies) + ".reorder"), ct));
    }

    /// <summary>Toggle a rule on/off without deleting it.</summary>
    [HttpPatch("{id:guid}/policies/{ruleId}")]
    public async Task<ActionResult<TenantConfiguration>> TogglePolicyRule(
        Guid id,
        string ruleId,
        [FromBody] TogglePolicyRequest body,
        CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var cfg = await _configService.GetConfigurationAsync(id, ct);
        var rule = cfg.Policies?.FirstOrDefault(r => r.Id == ruleId);
        if (rule is null) return NotFound();

        rule.Active = body?.Active ?? rule.Active;
        return Ok(await _configService.PatchPoliciesAsync(id, cfg.Policies!, Audit(nameof(TenantConfiguration.Policies) + ".toggle"), ct));
    }

    [HttpDelete("{id:guid}/policies/{ruleId}")]
    public async Task<ActionResult<TenantConfiguration>> DeletePolicyRule(Guid id, string ruleId, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var cfg = await _configService.GetConfigurationAsync(id, ct);
        var rule = cfg.Policies?.FirstOrDefault(r => r.Id == ruleId);
        if (rule is null) return NotFound();

        var remaining = cfg.Policies!.Where(r => r.Id != ruleId).ToList();
        return Ok(await _configService.PatchPoliciesAsync(id, remaining, Audit(nameof(TenantConfiguration.Policies) + ".delete"), ct));
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

    [HttpPatch("{id:guid}/config/diagnostics")]
    public async Task<ActionResult<TenantConfiguration>> PatchTenantDiagnostics(Guid id, [FromBody] DiagnosticsConfig body, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var cfg = await _configService.GetConfigurationAsync(id, ct);
        cfg.Diagnostics = body;
        var errors = TenantConfigurationValidator.Validate(cfg);
        if (errors.Count > 0) return BadRequest(new { errors });
        return Ok(await _configService.PatchDiagnosticsConfigAsync(id, body, Audit(nameof(DiagnosticsConfig)), ct));
    }

    [HttpGet("{id:guid}/config/outbound-webhook")]
    public async Task<ActionResult<OutboundWebhookSettings>> GetOutboundWebhook(Guid id, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();
        var settings = TenantSettingsJson.Parse(tenant.ConfigJson).OutboundWebhook;
        return Ok(settings);
    }

    [HttpPut("{id:guid}/config/outbound-webhook")]
    public async Task<ActionResult<OutboundWebhookSettings>> PutOutboundWebhook(
        Guid id,
        [FromBody] OutboundWebhookSettings body,
        CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();

        var root = TenantSettingsJson.Parse(tenant.ConfigJson);
        root.OutboundWebhook = new OutboundWebhookSettings
        {
            Enabled = body.Enabled,
            Url = string.IsNullOrWhiteSpace(body.Url) ? null : body.Url.Trim(),
            Secret = string.IsNullOrWhiteSpace(body.Secret) ? null : body.Secret.Trim(),
            Events = (body.Events is { Count: > 0 }
                ? body.Events.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : ["approval.required", "incident.created", "handoff.requested", "conversation.resolved"])
        };

        tenant.ConfigJson = TenantSettingsJson.Stringify(root);
        await _db.SaveChangesAsync(ct);
        return Ok(root.OutboundWebhook);
    }

    [HttpPost("{id:guid}/config/outbound-webhook/test")]
    public async Task<IActionResult> TestOutboundWebhook(
        Guid id,
        [FromServices] IOutboundWebhookService webhookService,
        [FromServices] ChatOpsApprovalTokenService tokenService,
        CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();

        var settings = TenantSettingsJson.Parse(tenant.ConfigJson).OutboundWebhook;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Url))
            return BadRequest(new { message = "Habilitá el webhook y guardá una URL válida antes de enviar la prueba." });

        var sampleIncidentId = Guid.NewGuid();
        var approveToken = tokenService.CreateToken(id, sampleIncidentId, "approve");
        var rejectToken = tokenService.CreateToken(id, sampleIncidentId, "reject");
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var approveUrl = $"{baseUrl}/api/support/chatops/incidents/{sampleIncidentId}/decision?tenantId={id}&action=approve&token={approveToken}";
        var rejectUrl = $"{baseUrl}/api/support/chatops/incidents/{sampleIncidentId}/decision?tenantId={id}&action=reject&token={rejectToken}";

        var payload = new
        {
            incidentId = sampleIncidentId,
            branchName = "Prueba de conectividad ChatOps 1-Click (HiveOps)",
            approvePath = approveUrl,
            rejectPath = rejectUrl,
            slackBlocks = SupervisionNotificationService.BuildSlackApprovalBlocks(
                id, sampleIncidentId, "Prueba de conectividad ChatOps 1-Click", approveUrl, rejectUrl, approveToken, rejectToken),
            teamsCard = SupervisionNotificationService.BuildTeamsApprovalCard(
                id, sampleIncidentId, "Prueba de conectividad ChatOps 1-Click", approveUrl, rejectUrl),
            at = DateTimeOffset.UtcNow
        };

        await webhookService.SendEventAsync(id, "approval.required", payload, ct);
        return Ok(new { ok = true, message = "Evento de prueba enviado al webhook configurado." });
    }

    private TenantConfigAuditInfo Audit(string section) =>
        new(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name, section);
}

// DTOs
public sealed record TenantDto(Guid Id, string Name, string? Industry, bool IsActive);
public sealed record InterpretPolicyRequest(string? Text);
public sealed record AddPolicyRequest(string? Text, PolicyRule? Rule, string? InsertBeforeRuleId);
public sealed record ReorderPoliciesRequest(List<string>? RuleIds);
public sealed record TogglePolicyRequest(bool Active);
