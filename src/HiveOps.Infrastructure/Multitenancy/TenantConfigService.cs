using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Models;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Infrastructure.Multitenancy;

public sealed class TenantConfigService : ITenantConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantConfigService> _logger;
    private readonly ITenantConfigurationAuditLogger _auditLogger;

    public TenantConfigService(
        AppDbContext db,
        IMemoryCache cache,
        ILogger<TenantConfigService> logger,
        ITenantConfigurationAuditLogger auditLogger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
        _auditLogger = auditLogger;
    }

    public Task<TenantConfiguration> UpsertConfigurationAsync(Guid tenantId, TenantConfiguration configuration, CancellationToken ct = default)
        => UpsertConfigurationAsync(tenantId, configuration, audit: null, ct);

    public async Task<TenantConfiguration> UpsertConfigurationAsync(Guid tenantId, TenantConfiguration configuration, TenantConfigAuditInfo? audit, CancellationToken ct = default)
    {
        NormalizeIncomingConfiguration(configuration);

        var errors = TenantConfigurationValidator.Validate(configuration);
        if (errors.Count > 0)
            throw new InvalidOperationException($"Invalid tenant configuration: {string.Join(" | ", errors)}");

        await InvalidateAsync(tenantId, ct);

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId && t.IsActive, ct)
            ?? throw new InvalidOperationException("Tenant not found or inactive.");

        var settings = ParseSettings(tenant.ConfigJson);
        TenantConfigurationMigrator.EnsureMigrated(ref settings);

        settings.RuntimeConfiguration = configuration;
        settings.SchemaVersion = TenantConfigurationSchema.Current;
        settings.RuntimeConfiguration.ConfigurationSchemaVersion = TenantConfigurationSchema.Current;

        tenant.ConfigJson = JsonSerializer.Serialize(settings, JsonOptions);

        await _db.SaveChangesAsync(ct);
        await InvalidateAsync(tenantId, ct);
        _logger.LogInformation("Tenant configuration updated for tenant {TenantId}", tenantId);

        if (audit is not null)
        {
            _auditLogger.Record(new TenantConfigurationAuditPayload(
                tenantId,
                audit.Section,
                audit.Subject,
                audit.Action ?? "Upsert full TenantConfiguration"));
        }

        var result = DuplicateConfig(settings.RuntimeConfiguration);
        _cache.Set(BuildCacheKey(tenantId), result, TimeSpan.FromMinutes(5));
        return result;
    }

    public async Task<TenantConfiguration> GetConfigurationAsync(Guid tenantId, CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(tenantId);
        if (_cache.TryGetValue(cacheKey, out TenantConfiguration? cached) && cached is not null)
            return DuplicateConfig(cached);

        var tenant = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.IsActive, ct);

        if (tenant is null)
            return TenantConfiguration.CreateDefault();

        var settings = ParseSettings(tenant.ConfigJson);
        var hydrated = TenantConfigurationMigrator.HydrateEffective(settings);
        var dup = DuplicateConfig(hydrated);

        _cache.Set(cacheKey, dup, TimeSpan.FromMinutes(5));
        return DuplicateConfig(dup);
    }

    private async Task<TenantConfiguration> LoadEffectiveFromDatabaseAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.IsActive, ct);

        if (tenant is null)
            return TenantConfiguration.CreateDefault();

        var settings = ParseSettings(tenant.ConfigJson);
        return DuplicateConfig(TenantConfigurationMigrator.HydrateEffective(settings));
    }

    public Task<TenantConfiguration> PatchAgentConfigAsync(Guid tenantId, AgentConfig patch, CancellationToken ct = default)
        => PatchAgentConfigAsync(tenantId, patch, null, ct);

    public async Task<TenantConfiguration> PatchAgentConfigAsync(Guid tenantId, AgentConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default)
    {
        var current = await LoadEffectiveFromDatabaseAsync(tenantId, ct);
        current.Agent = patch;
        return await UpsertConfigurationAsync(tenantId, current, audit ?? new TenantConfigAuditInfo(null, nameof(AgentConfig), "Patch agent"), ct);
    }

    public Task<TenantConfiguration> PatchToolConfigAsync(Guid tenantId, ToolConfig patch, CancellationToken ct = default)
        => PatchToolConfigAsync(tenantId, patch, null, ct);

    public async Task<TenantConfiguration> PatchToolConfigAsync(Guid tenantId, ToolConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default)
    {
        var current = await LoadEffectiveFromDatabaseAsync(tenantId, ct);
        current.Tools = patch;
        return await UpsertConfigurationAsync(tenantId, current, audit ?? new TenantConfigAuditInfo(null, nameof(ToolConfig), "Patch tools"), ct);
    }

    public Task<TenantConfiguration> PatchBusinessConfigAsync(Guid tenantId, BusinessConfig patch, CancellationToken ct = default)
        => PatchBusinessConfigAsync(tenantId, patch, null, ct);

    public async Task<TenantConfiguration> PatchBusinessConfigAsync(Guid tenantId, BusinessConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default)
    {
        var current = await LoadEffectiveFromDatabaseAsync(tenantId, ct);
        current.Business = patch;
        return await UpsertConfigurationAsync(tenantId, current, audit ?? new TenantConfigAuditInfo(null, nameof(BusinessConfig), "Patch business"), ct);
    }

    public Task<TenantConfiguration> PatchLlmConfigAsync(Guid tenantId, LlmConfig patch, CancellationToken ct = default)
        => PatchLlmConfigAsync(tenantId, patch, null, ct);

    public async Task<TenantConfiguration> PatchLlmConfigAsync(Guid tenantId, LlmConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default)
    {
        var current = await LoadEffectiveFromDatabaseAsync(tenantId, ct);
        current.Llm = patch;
        return await UpsertConfigurationAsync(tenantId, current, audit ?? new TenantConfigAuditInfo(null, nameof(LlmConfig), "Patch llm"), ct);
    }

    public Task<TenantConfiguration> PatchDeployGitConfigAsync(Guid tenantId, DeployGitConfig patch, CancellationToken ct = default)
        => PatchDeployGitConfigAsync(tenantId, patch, null, ct);

    public async Task<TenantConfiguration> PatchDeployGitConfigAsync(Guid tenantId, DeployGitConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default)
    {
        var current = await LoadEffectiveFromDatabaseAsync(tenantId, ct);
        current.DeployGit = patch;
        return await UpsertConfigurationAsync(tenantId, current, audit ?? new TenantConfigAuditInfo(null, nameof(DeployGitConfig), "Patch deploy/git"), ct);
    }

    public Task<TenantConfiguration> PatchPoliciesAsync(Guid tenantId, List<PolicyRule> policies, CancellationToken ct = default)
        => PatchPoliciesAsync(tenantId, policies, null, ct);

    public async Task<TenantConfiguration> PatchPoliciesAsync(Guid tenantId, List<PolicyRule> policies, TenantConfigAuditInfo? audit, CancellationToken ct = default)
    {
        var current = await LoadEffectiveFromDatabaseAsync(tenantId, ct);
        current.Policies = policies ?? [];
        return await UpsertConfigurationAsync(tenantId, current, audit ?? new TenantConfigAuditInfo(null, nameof(TenantConfiguration.Policies), "Patch policies"), ct);
    }

    public Task<TenantConfiguration> PatchChannelConfigAsync(Guid tenantId, ChannelConfig patch, CancellationToken ct = default)
        => PatchChannelConfigAsync(tenantId, patch, null, ct);

    public async Task<TenantConfiguration> PatchChannelConfigAsync(Guid tenantId, ChannelConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default)
    {
        var current = await LoadEffectiveFromDatabaseAsync(tenantId, ct);
        current.Channel = patch;
        return await UpsertConfigurationAsync(tenantId, current, audit ?? new TenantConfigAuditInfo(null, nameof(ChannelConfig), "Patch channel"), ct);
    }

    public Task<TenantConfiguration> PatchEscalationConfigAsync(Guid tenantId, EscalationConfig patch, CancellationToken ct = default)
        => PatchEscalationConfigAsync(tenantId, patch, null, ct);

    public async Task<TenantConfiguration> PatchEscalationConfigAsync(Guid tenantId, EscalationConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default)
    {
        var current = await LoadEffectiveFromDatabaseAsync(tenantId, ct);
        current.Escalation = patch;
        return await UpsertConfigurationAsync(tenantId, current, audit ?? new TenantConfigAuditInfo(null, nameof(EscalationConfig), "Patch escalation"), ct);
    }

    public Task InvalidateAsync(Guid tenantId, CancellationToken ct = default)
    {
        _cache.Remove(BuildCacheKey(tenantId));
        return Task.CompletedTask;
    }

    private static string BuildCacheKey(Guid tenantId) => $"tenant-config:{tenantId:D}";

    private static void NormalizeIncomingConfiguration(TenantConfiguration configuration)
    {
        if (configuration.ConfigurationSchemaVersion <= 0)
            configuration.ConfigurationSchemaVersion = TenantConfigurationSchema.Current;
    }

    private static TenantAdminSettings ParseSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new TenantAdminSettings();

        try
        {
            return JsonSerializer.Deserialize<TenantAdminSettings>(json, JsonOptions) ?? new TenantAdminSettings();
        }
        catch
        {
            return new TenantAdminSettings();
        }
    }

    private static TenantConfiguration DuplicateConfig(TenantConfiguration source)
        => JsonSerializer.Deserialize<TenantConfiguration>(JsonSerializer.Serialize(source, JsonOptions), JsonOptions)
           ?? TenantConfiguration.CreateDefault();
}
