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

    Task<TenantConfiguration> PatchDiagnosticsConfigAsync(Guid tenantId, DiagnosticsConfig patch, CancellationToken ct = default);

    Task<TenantConfiguration> PatchDiagnosticsConfigAsync(Guid tenantId, DiagnosticsConfig patch, TenantConfigAuditInfo? audit, CancellationToken ct = default);

    /// <summary>Loads the full <see cref="TenantAdminSettings"/> envelope (migrated), not just the runtime section.</summary>
    Task<TenantAdminSettings> GetAdminSettingsAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Persists admin-editable sections of the envelope. <see cref="TenantAdminSettings.RuntimeConfiguration"/>
    /// is always preserved from the currently stored envelope — admin-settings saves never touch runtime config.
    /// </summary>
    Task<TenantAdminSettings> SaveAdminSettingsAsync(Guid tenantId, TenantAdminSettings settings, TenantConfigAuditInfo? audit, CancellationToken ct = default);

    /// <summary>True when a (protected) deploy git access token is stored for the tenant.</summary>
    Task<bool> HasDeployGitTokenAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Stores the deploy git access token encrypted; null/empty clears it.</summary>
    Task SetDeployGitTokenAsync(Guid tenantId, string? plaintextToken, CancellationToken ct = default);

    /// <summary>Returns the decrypted deploy git access token for internal use (never expose via API).</summary>
    Task<string?> GetDeployGitTokenAsync(Guid tenantId, CancellationToken ct = default);

    Task InvalidateAsync(Guid tenantId, CancellationToken ct = default);
}
