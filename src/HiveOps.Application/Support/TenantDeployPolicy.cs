using HiveOps.Application.Interfaces;

namespace HiveOps.Application.Support;

/// <summary>Centralizes deploy permission checks sourced from tenant <see cref="Configuration.DeployGitConfig"/>.</summary>
public static class TenantDeployPolicy
{
    public static async Task<bool> IsAutoDeployEnabledAsync(ITenantConfigService configs, Guid tenantId, CancellationToken ct = default)
    {
        var c = await configs.GetConfigurationAsync(tenantId, ct);
        return c.DeployGit.AutoDeployEnabled;
    }

    public static async Task<bool> IsManualDeployAllowedAsync(ITenantConfigService configs, Guid tenantId, CancellationToken ct = default)
    {
        var c = await configs.GetConfigurationAsync(tenantId, ct);
        return c.DeployGit.ManualDeployAllowed;
    }
}
