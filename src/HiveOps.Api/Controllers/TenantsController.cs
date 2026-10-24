using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using HiveOps.Api.Authentication;
using HiveOps.Api.Services;
using HiveOps.Api.Utilities;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Models;
using HiveOps.Application.Policies;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Git;
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
    private readonly IDynamicConnectionStringResolver _connResolver;
    private readonly IDataProtector _connProtector;

    public TenantsController(
        AppDbContext db,
        TenantContext tenantContext,
        ITenantConfigService configService,
        IDynamicConnectionStringResolver connResolver,
        IDataProtectionProvider dataProtectionProvider)
    {
        _db = db;
        _tenantContext = tenantContext;
        _configService = configService;
        _connResolver = connResolver;
        _connProtector = dataProtectionProvider.CreateProtector(
            DynamicConnectionStringResolver.TenantConnectionStringDataProtectionPurpose);
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

    [HttpGet("{id:guid}/config/deploy-git")]
    public async Task<IActionResult> GetTenantDeployGit(Guid id, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        var cfg = await _configService.GetConfigurationAsync(id, ct);
        var hasToken = await _configService.HasDeployGitTokenAsync(id, ct);
        return Ok(new
        {
            autoDeployEnabled = cfg.DeployGit.AutoDeployEnabled,
            manualDeployAllowed = cfg.DeployGit.ManualDeployAllowed,
            gitRepositoryUrl = cfg.DeployGit.GitRepositoryUrl,
            gitDefaultBranch = cfg.DeployGit.GitDefaultBranch,
            hasAccessToken = hasToken
        });
    }

    [HttpPatch("{id:guid}/config/deploy-git")]
    public async Task<ActionResult<TenantConfiguration>> PatchTenantDeployGit(Guid id, [FromBody] DeployGitPatchRequest body, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        if (body is null) return BadRequest(new { errors = new[] { "Payload requerido." } });

        var patch = new DeployGitConfig
        {
            AutoDeployEnabled = body.AutoDeployEnabled,
            ManualDeployAllowed = body.ManualDeployAllowed,
            GitRepositoryUrl = body.GitRepositoryUrl,
            GitDefaultBranch = body.GitDefaultBranch,
            WorkingDirectoryRelativePath = body.WorkingDirectoryRelativePath
        };

        var cfg = await _configService.GetConfigurationAsync(id, ct);
        cfg.DeployGit = patch;
        var errors = TenantConfigurationValidator.Validate(cfg);
        if (errors.Count > 0) return BadRequest(new { errors });

        var result = await _configService.PatchDeployGitConfigAsync(id, patch, Audit(nameof(DeployGitConfig)), ct);

        // Git access token is stored encrypted outside RuntimeConfiguration; never echoed back.
        if (!string.IsNullOrWhiteSpace(body.GitAccessToken))
            await _configService.SetDeployGitTokenAsync(id, body.GitAccessToken.Trim(), ct);
        else if (body.ClearGitAccessToken)
            await _configService.SetDeployGitTokenAsync(id, null, ct);

        return Ok(result);
    }

    [HttpPost("{id:guid}/config/deploy-git/test")]
    public async Task<IActionResult> TestTenantDeployGit(Guid id, [FromServices] IGitService gitService, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        if (gitService is not TenantGitService tenantGit)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { ok = false, message = "La prueba de conexión Git no está disponible en este despliegue." });

        var (ok, message) = await tenantGit.TestRepositoryAccessAsync(id, ct);
        return ok ? Ok(new { ok, message }) : BadRequest(new { ok, message });
    }

    // ── Target business database (the tenant's own DB the agent diagnoses/fixes) ──

    /// <summary>
    /// Returns masked details of the tenant's dedicated database (host/database/user).
    /// The password and the full connection string are never returned.
    /// </summary>
    [HttpGet("{id:guid}/config/target-database")]
    public async Task<IActionResult> GetTargetDatabase(Guid id, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        if (!_tenantContext.IsResolved) _tenantContext.SetTenant(id);

        var encrypted = await _db.Tenants.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => t.EncryptedConnectionString)
            .FirstOrDefaultAsync(ct);
        if (encrypted is null) return NotFound();

        if (string.IsNullOrWhiteSpace(encrypted))
            return Ok(new TargetDatabaseStatusDto(false, null, null, null));

        try
        {
            var plain = Encoding.UTF8.GetString(_connProtector.Unprotect(Convert.FromBase64String(encrypted.Trim())));
            var b = new NpgsqlConnectionStringBuilder(plain);
            return Ok(new TargetDatabaseStatusDto(true, b.Host, b.Database, b.Username));
        }
        catch
        {
            return Ok(new TargetDatabaseStatusDto(true, null, null, null));
        }
    }

    /// <summary>
    /// Sets (or clears with an empty body) the tenant's dedicated database.
    /// The connection string is probed before saving and stored encrypted; it is
    /// never returned by any endpoint.
    /// </summary>
    [HttpPut("{id:guid}/config/target-database")]
    public async Task<IActionResult> PutTargetDatabase(Guid id, [FromBody] TargetDatabaseRequest? request, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        if (!_tenantContext.IsResolved) _tenantContext.SetTenant(id);

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();

        var cs = request?.ConnectionString?.Trim();
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (tenant.EncryptedConnectionString is null)
                return Ok(new { ok = true, configured = false });
            tenant.EncryptedConnectionString = null;
            await _db.SaveChangesAsync(ct);
            _connResolver.Invalidate(id);
            await _configService.InvalidateAsync(id, ct);
            return Ok(new { ok = true, configured = false });
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(cs) { Timeout = 5 };
            await using var probe = new NpgsqlConnection(builder.ConnectionString);
            await probe.OpenAsync(ct);
        }
        catch (Exception ex)
        {
            return BadRequest(new { errors = new[] { "No se pudo conectar a la base de datos. Revisá host, puerto, usuario y que sea accesible desde HiveOps." }, detail = ex.Message });
        }

        tenant.EncryptedConnectionString = Convert.ToBase64String(
            _connProtector.Protect(Encoding.UTF8.GetBytes(cs)));
        await _db.SaveChangesAsync(ct);
        _connResolver.Invalidate(id);
        await _configService.InvalidateAsync(id, ct);

        var parsed = new NpgsqlConnectionStringBuilder(cs);
        return Ok(new TargetDatabaseStatusDto(true, parsed.Host, parsed.Database, parsed.Username));
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

    // ── Friendly tenant admin settings (bot persona, messages, WhatsApp, handoff) ──

    [HttpGet("{id:guid}/admin-settings")]
    public async Task<ActionResult<TenantAdminSettingsDto>> GetAdminSettings(Guid id, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        // Privileged users bypass tenant resolution in middleware; resolve explicitly
        // so RLS (app.tenant_id) sees this tenant for BusinessConfigs reads/writes.
        if (!_tenantContext.IsResolved) _tenantContext.SetTenant(id);
        var tenantExists = await _db.Tenants.AsNoTracking().AnyAsync(t => t.Id == id, ct);
        if (!tenantExists) return NotFound();

        var settings = await _configService.GetAdminSettingsAsync(id, ct);
        var businessConfig = await _db.BusinessConfigs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == id, ct);

        return Ok(ToAdminSettingsDto(settings, businessConfig?.MaxRetryBeforeHandoff ?? 2));
    }

    [HttpPatch("{id:guid}/admin-settings")]
    public async Task<ActionResult<TenantAdminSettingsDto>> PatchAdminSettings(Guid id, [FromBody] TenantAdminSettingsDto body, CancellationToken ct)
    {
        if (!IsAdminOrSuperAdmin && id != _tenantContext.TenantId) return Forbid();
        if (body is null) return BadRequest(new { errors = new[] { "Payload requerido." } });
        if (!_tenantContext.IsResolved) _tenantContext.SetTenant(id);

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();

        var errors = new List<string>();
        var settings = await _configService.GetAdminSettingsAsync(id, ct);

        if (body.Assistant is { } assistant)
        {
            if (assistant.Name is not null)
            {
                if (string.IsNullOrWhiteSpace(assistant.Name)) errors.Add("assistant.name no puede estar vacío.");
                else settings.BotBehavior.AssistantName = assistant.Name.Trim();
            }
            if (assistant.Tone is not null)
            {
                if (string.IsNullOrWhiteSpace(assistant.Tone)) errors.Add("assistant.tone no puede estar vacío.");
                else settings.BotBehavior.Tone = assistant.Tone.Trim();
            }
            if (assistant.Language is not null)
            {
                if (string.IsNullOrWhiteSpace(assistant.Language)) errors.Add("assistant.language no puede estar vacío.");
                else settings.BotBehavior.ResponseLanguage = assistant.Language.Trim();
            }
        }

        if (body.Messages is { } messages)
        {
            if (messages.Welcome is not null)
            {
                if (string.IsNullOrWhiteSpace(messages.Welcome)) errors.Add("messages.welcome no puede estar vacío.");
                else settings.Phrases.WelcomeMessage = messages.Welcome.Trim();
            }
            if (messages.Fallback is not null)
            {
                if (string.IsNullOrWhiteSpace(messages.Fallback)) errors.Add("messages.fallback no puede estar vacío.");
                else settings.Phrases.FallbackMessage = messages.Fallback.Trim();
            }
            if (messages.Handoff is not null)
            {
                if (string.IsNullOrWhiteSpace(messages.Handoff)) errors.Add("messages.handoff no puede estar vacío.");
                else settings.Phrases.HumanHandoffMessage = messages.Handoff.Trim();
            }
        }

        if (body.WhatsApp is { } wa)
        {
            if (wa.PhoneNumber is not null)
                settings.WhatsApp.PhoneNumber = wa.PhoneNumber.Trim();
            if (wa.PhoneNumberId is not null)
                settings.WhatsApp.PhoneNumberId = wa.PhoneNumberId.Trim();
            if (wa.Provider is not null && !string.IsNullOrWhiteSpace(wa.Provider))
                settings.WhatsApp.Provider = wa.Provider.Trim();

            if (!string.IsNullOrWhiteSpace(wa.AccessToken))
            {
                // Mirror AdminTenantsController access-token save semantics.
                settings.WhatsApp.ApiKey = wa.AccessToken.Trim();
                settings.WhatsApp.ApiKeyUpdatedAtUtc = DateTimeOffset.UtcNow;
                if (string.IsNullOrWhiteSpace(settings.WhatsApp.Provider))
                    settings.WhatsApp.Provider = "MetaCloud";
                settings.WhatsApp.Enabled = !string.IsNullOrWhiteSpace(settings.WhatsApp.PhoneNumber);
            }

            // Explicit enabled flag wins over the token-side auto-enable above.
            if (wa.Enabled.HasValue)
                settings.WhatsApp.Enabled = wa.Enabled.Value;
            if (wa.InteractiveButtons.HasValue)
                settings.WhatsApp.EnableInteractiveButtons = wa.InteractiveButtons.Value;
            if (wa.InteractiveMenu.HasValue)
                settings.WhatsApp.EnableInteractiveMenu = wa.InteractiveMenu.Value;
        }

        int? maxRetries = null;
        if (body.Handoff is { } handoff)
        {
            if (handoff.FrustrationEscalation.HasValue)
                settings.BotBehavior.EnableFrustrationEscalation = handoff.FrustrationEscalation.Value;
            if (handoff.TriggerKeywords is not null)
                settings.BotBehavior.EscalationTriggerKeywords = handoff.TriggerKeywords
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Select(k => k.Trim())
                    .Select(k => k.Length > 100 ? k[..100] : k)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(50)
                    .ToList();
            if (handoff.SlaMinutes.HasValue)
                settings.BotBehavior.HumanSlaThresholdMinutes = Math.Clamp(handoff.SlaMinutes.Value, 5, 1440);
            if (handoff.MaxRetriesBeforeHandoff.HasValue)
                maxRetries = Math.Clamp(handoff.MaxRetriesBeforeHandoff.Value, 1, 10);
        }

        if (errors.Count > 0) return BadRequest(new { errors });

        // Keep the BusinessConfigs table in sync — IncomingMessageHandler reads
        // WelcomeMessage/FallbackMessage/MaxRetryBeforeHandoff from there.
        var businessConfig = await _db.BusinessConfigs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.TenantId == id, ct);

        if (businessConfig is null)
        {
            businessConfig = new HiveOps.Domain.Entities.BusinessConfig
            {
                Id = Guid.NewGuid(),
                TenantId = id,
                WelcomeMessage = settings.Phrases.WelcomeMessage,
                FallbackMessage = settings.Phrases.FallbackMessage,
                MaxRetryBeforeHandoff = maxRetries ?? 2,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _db.BusinessConfigs.Add(businessConfig);
        }
        else
        {
            businessConfig.WelcomeMessage = settings.Phrases.WelcomeMessage;
            businessConfig.FallbackMessage = settings.Phrases.FallbackMessage;
            if (maxRetries.HasValue)
                businessConfig.MaxRetryBeforeHandoff = maxRetries.Value;
            businessConfig.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);

        var saved = await _configService.SaveAdminSettingsAsync(id, settings, Audit("AdminSettings"), ct);
        return Ok(ToAdminSettingsDto(saved, businessConfig.MaxRetryBeforeHandoff));
    }

    private static TenantAdminSettingsDto ToAdminSettingsDto(TenantAdminSettings s, int maxRetriesBeforeHandoff) => new()
    {
        Assistant = new AdminAssistantDto
        {
            Name = s.BotBehavior.AssistantName,
            Tone = s.BotBehavior.Tone,
            Language = s.BotBehavior.ResponseLanguage
        },
        Messages = new AdminMessagesDto
        {
            Welcome = s.Phrases.WelcomeMessage,
            Fallback = s.Phrases.FallbackMessage,
            Handoff = s.Phrases.HumanHandoffMessage
        },
        WhatsApp = new AdminWhatsAppDto
        {
            Enabled = s.WhatsApp.Enabled,
            PhoneNumber = s.WhatsApp.PhoneNumber,
            PhoneNumberId = s.WhatsApp.PhoneNumberId,
            Provider = s.WhatsApp.Provider,
            HasAccessToken = !string.IsNullOrWhiteSpace(s.WhatsApp.ApiKey),
            AccessTokenUpdatedAtUtc = s.WhatsApp.ApiKeyUpdatedAtUtc,
            InteractiveButtons = s.WhatsApp.EnableInteractiveButtons,
            InteractiveMenu = s.WhatsApp.EnableInteractiveMenu
        },
        Handoff = new AdminHandoffDto
        {
            FrustrationEscalation = s.BotBehavior.EnableFrustrationEscalation,
            TriggerKeywords = s.BotBehavior.EscalationTriggerKeywords,
            MaxRetriesBeforeHandoff = maxRetriesBeforeHandoff,
            SlaMinutes = s.BotBehavior.HumanSlaThresholdMinutes
        }
    };

    private TenantConfigAuditInfo Audit(string section) =>
        new(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name, section);
}

// DTOs
public sealed record TenantDto(Guid Id, string Name, string? Industry, bool IsActive);
public sealed record InterpretPolicyRequest(string? Text);
public sealed record AddPolicyRequest(string? Text, PolicyRule? Rule, string? InsertBeforeRuleId);
public sealed record ReorderPoliciesRequest(List<string>? RuleIds);
public sealed record TogglePolicyRequest(bool Active);

/// <summary>
/// PATCH body for <c>/config/deploy-git</c>: all <see cref="DeployGitConfig"/> fields plus
/// write-only token handling. <see cref="GitAccessToken"/> (when non-empty) stores a new
/// encrypted token; <see cref="ClearGitAccessToken"/> removes it. The token is never returned.
/// </summary>
public sealed class DeployGitPatchRequest
{
    public bool AutoDeployEnabled { get; set; } = true;
    public bool ManualDeployAllowed { get; set; } = true;
    public string? GitRepositoryUrl { get; set; }
    public string GitDefaultBranch { get; set; } = "main";
    public string? WorkingDirectoryRelativePath { get; set; }
    public string? GitAccessToken { get; set; }
    public bool ClearGitAccessToken { get; set; }
}

/// <summary>Friendly tenant-facing view of <see cref="TenantAdminSettings"/> (no secrets).</summary>
public sealed class TenantAdminSettingsDto
{
    public AdminAssistantDto? Assistant { get; set; }
    public AdminMessagesDto? Messages { get; set; }
    public AdminWhatsAppDto? WhatsApp { get; set; }
    public AdminHandoffDto? Handoff { get; set; }
}

public sealed class AdminAssistantDto
{
    public string? Name { get; set; }
    public string? Tone { get; set; }
    public string? Language { get; set; }
}

public sealed class AdminMessagesDto
{
    public string? Welcome { get; set; }
    public string? Fallback { get; set; }
    public string? Handoff { get; set; }
}

public sealed class AdminWhatsAppDto
{
    public bool? Enabled { get; set; }
    public string? PhoneNumber { get; set; }
    public string? PhoneNumberId { get; set; }
    public string? Provider { get; set; }
    /// <summary>GET-only: whether a WhatsApp access token is stored. The token itself is never returned.</summary>
    public bool HasAccessToken { get; set; }
    public DateTimeOffset? AccessTokenUpdatedAtUtc { get; set; }
    public bool? InteractiveButtons { get; set; }
    public bool? InteractiveMenu { get; set; }
    /// <summary>PATCH-only: new WhatsApp access token. Applied only when non-empty; never serialized back.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccessToken { get; set; }
}

public sealed class AdminHandoffDto
{
    public bool? FrustrationEscalation { get; set; }
    public List<string>? TriggerKeywords { get; set; }
    /// <summary>Maps to the BusinessConfigs table (real retry threshold used by IncomingMessageHandler).</summary>
    public int? MaxRetriesBeforeHandoff { get; set; }
    public int? SlaMinutes { get; set; }
}

public sealed record TargetDatabaseStatusDto(bool Configured, string? Host, string? Database, string? Username);
public sealed record TargetDatabaseRequest(string? ConnectionString);
