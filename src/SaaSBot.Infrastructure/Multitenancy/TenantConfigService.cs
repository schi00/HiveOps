using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SaaSBot.Application.Configuration;
using SaaSBot.Application.Interfaces;
using SaaSBot.Application.Models;
using SaaSBot.Infrastructure.Persistence;

namespace SaaSBot.Infrastructure.Multitenancy;

public sealed class TenantConfigService : ITenantConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantConfigService> _logger;

    public TenantConfigService(AppDbContext db, IMemoryCache cache, ILogger<TenantConfigService> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task<TenantConfiguration> GetConfigurationAsync(Guid tenantId, CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(tenantId);
        if (_cache.TryGetValue(cacheKey, out TenantConfiguration? cached) && cached is not null)
            return cached;

        var tenant = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.IsActive, ct);

        if (tenant is null)
            return TenantConfiguration.CreateDefault();

        var config = ExtractConfiguration(tenant.ConfigJson);
        _cache.Set(cacheKey, config, TimeSpan.FromMinutes(5));
        return config;
    }

    public async Task<TenantConfiguration> UpsertConfigurationAsync(Guid tenantId, TenantConfiguration configuration, CancellationToken ct = default)
    {
        var errors = TenantConfigurationValidator.Validate(configuration);
        if (errors.Count > 0)
            throw new InvalidOperationException($"Invalid tenant configuration: {string.Join(" | ", errors)}");

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId && t.IsActive, ct)
            ?? throw new InvalidOperationException("Tenant not found or inactive.");

        var settings = ParseSettings(tenant.ConfigJson);
        settings.RuntimeConfiguration = configuration;
        tenant.ConfigJson = JsonSerializer.Serialize(settings, JsonOptions);

        await _db.SaveChangesAsync(ct);
        await InvalidateAsync(tenantId, ct);
        _logger.LogInformation("Tenant configuration updated for tenant {TenantId}", tenantId);
        return configuration;
    }

    public async Task<TenantConfiguration> PatchAgentConfigAsync(Guid tenantId, AgentConfig patch, CancellationToken ct = default)
    {
        var current = await GetConfigurationAsync(tenantId, ct);
        current.Agent = patch;
        return await UpsertConfigurationAsync(tenantId, current, ct);
    }

    public async Task<TenantConfiguration> PatchToolConfigAsync(Guid tenantId, ToolConfig patch, CancellationToken ct = default)
    {
        var current = await GetConfigurationAsync(tenantId, ct);
        current.Tools = patch;
        return await UpsertConfigurationAsync(tenantId, current, ct);
    }

    public async Task<TenantConfiguration> PatchBusinessConfigAsync(Guid tenantId, BusinessConfig patch, CancellationToken ct = default)
    {
        var current = await GetConfigurationAsync(tenantId, ct);
        current.Business = patch;
        return await UpsertConfigurationAsync(tenantId, current, ct);
    }

    public Task InvalidateAsync(Guid tenantId, CancellationToken ct = default)
    {
        _cache.Remove(BuildCacheKey(tenantId));
        return Task.CompletedTask;
    }

    private static string BuildCacheKey(Guid tenantId) => $"tenant-config:{tenantId:D}";

    private static TenantConfiguration ExtractConfiguration(string? configJson)
    {
        var settings = ParseSettings(configJson);
        return settings.RuntimeConfiguration ?? TenantConfiguration.CreateDefault();
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
}
