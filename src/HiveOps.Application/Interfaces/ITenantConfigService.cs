using HiveOps.Application.Configuration;
using HiveOps.Application.Models;

namespace HiveOps.Application.Interfaces;

public interface ITenantConfigService
{
    Task<TenantConfiguration> GetConfigurationAsync(Guid tenantId, CancellationToken ct = default);

    Task<TenantConfiguration> UpsertConfigurationAsync(Guid tenantId, TenantConfiguration configuration, CancellationToken ct = default);

    Task<TenantConfiguration> UpsertConfigurationAsync(Guid tenantId, TenantConfiguration configuration, TenantConfigAuditInfo? audit, CancellationToken ct = default);

    Task<TenantConfiguration> PatchAgentConfigAsync(Guid tenantId, AgentConfig patch, CancellationToken ct = default);

    Task<TenantConfiguration> PatchAgentConfigAsync(Guid tenantId, AgentConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default);

    Task<TenantConfiguration> PatchToolConfigAsync(Guid tenantId, ToolConfig patch, CancellationToken ct = default);

    Task<TenantConfiguration> PatchToolConfigAsync(Guid tenantId, ToolConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default);

    Task<TenantConfiguration> PatchBusinessConfigAsync(Guid tenantId, BusinessConfig patch, CancellationToken ct = default);

    Task<TenantConfiguration> PatchBusinessConfigAsync(Guid tenantId, BusinessConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default);

    Task<TenantConfiguration> PatchLlmConfigAsync(Guid tenantId, LlmConfig patch, CancellationToken ct = default);

    Task<TenantConfiguration> PatchLlmConfigAsync(Guid tenantId, LlmConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default);

    Task<TenantConfiguration> PatchDeployGitConfigAsync(Guid tenantId, DeployGitConfig patch, CancellationToken ct = default);

    Task<TenantConfiguration> PatchDeployGitConfigAsync(Guid tenantId, DeployGitConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default);

    Task<TenantConfiguration> PatchPoliciesAsync(Guid tenantId, List<PolicyRule> policies, CancellationToken ct = default);

    Task<TenantConfiguration> PatchPoliciesAsync(Guid tenantId, List<PolicyRule> policies, TenantConfigAuditInfo? audit, CancellationToken ct = default);

    Task<TenantConfiguration> PatchChannelConfigAsync(Guid tenantId, ChannelConfig patch, CancellationToken ct = default);

    Task<TenantConfiguration> PatchChannelConfigAsync(Guid tenantId, ChannelConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default);

    Task<TenantConfiguration> PatchEscalationConfigAsync(Guid tenantId, EscalationConfig patch, CancellationToken ct = default);

    Task<TenantConfiguration> PatchEscalationConfigAsync(Guid tenantId, EscalationConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default);

    Task InvalidateAsync(Guid tenantId, CancellationToken ct = default);
}
