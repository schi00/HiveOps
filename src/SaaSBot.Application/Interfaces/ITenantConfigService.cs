using SaaSBot.Application.Configuration;

namespace SaaSBot.Application.Interfaces;

public interface ITenantConfigService
{
    Task<TenantConfiguration> GetConfigurationAsync(Guid tenantId, CancellationToken ct = default);
    Task<TenantConfiguration> UpsertConfigurationAsync(Guid tenantId, TenantConfiguration configuration, CancellationToken ct = default);
    Task<TenantConfiguration> PatchAgentConfigAsync(Guid tenantId, AgentConfig patch, CancellationToken ct = default);
    Task<TenantConfiguration> PatchToolConfigAsync(Guid tenantId, ToolConfig patch, CancellationToken ct = default);
    Task<TenantConfiguration> PatchBusinessConfigAsync(Guid tenantId, BusinessConfig patch, CancellationToken ct = default);
    Task InvalidateAsync(Guid tenantId, CancellationToken ct = default);
}
