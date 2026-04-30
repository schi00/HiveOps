using HiveOps.Application.Models;

namespace HiveOps.Application.Configuration;

/// <summary>
/// Normalizes persisted <see cref="TenantAdminSettings"/> payloads and bumps <see cref="TenantAdminSettings.SchemaVersion"/>.
/// </summary>
public static class TenantConfigurationMigrator
{
    public static void EnsureMigrated(ref TenantAdminSettings settings)
    {
        if (settings.SchemaVersion <= 0)
            settings.SchemaVersion = TenantConfigurationSchema.LegacyV1;

        settings.RuntimeConfiguration ??= TenantConfiguration.CreateDefault();
        NormalizeRuntimeSections(settings.RuntimeConfiguration);

        if (settings.SchemaVersion >= TenantConfigurationSchema.Current)
        {
            if (settings.SchemaVersion > TenantConfigurationSchema.MaxKnown)
                settings.SchemaVersion = TenantConfigurationSchema.Current;
            return;
        }

        // v1 → current: additive sections populated with defaults via normalization.
        settings.SchemaVersion = TenantConfigurationSchema.Current;
        NormalizeRuntimeSections(settings.RuntimeConfiguration);
    }

    private static void NormalizeRuntimeSections(TenantConfiguration cfg)
    {
        cfg.Agent ??= new AgentConfig();
        cfg.Tools ??= new ToolConfig();
        cfg.Business ??= new BusinessConfig();
        cfg.Channel ??= new ChannelConfig();
        cfg.Escalation ??= new EscalationConfig();
        cfg.FeatureFlags ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        cfg.AbTesting ??= new ABTestingConfig();
        cfg.Policies ??= [];
        cfg.Llm ??= new LlmConfig();
        cfg.DeployGit ??= new DeployGitConfig();
        cfg.SecurityRefs ??= new TenantSecurityReferencesConfig();

        cfg.Llm.Model = string.IsNullOrWhiteSpace(cfg.Llm.Model) ? "gpt-4" : cfg.Llm.Model;
        cfg.Llm.Provider = string.IsNullOrWhiteSpace(cfg.Llm.Provider) ? "openai" : cfg.Llm.Provider;
        if (cfg.Llm.MaxOutputTokens is < 128 or > 32000)
            cfg.Llm.MaxOutputTokens = 2000;

        cfg.DeployGit.GitDefaultBranch = string.IsNullOrWhiteSpace(cfg.DeployGit.GitDefaultBranch)
            ? "main"
            : cfg.DeployGit.GitDefaultBranch;
    }

    public static TenantConfiguration HydrateEffective(TenantAdminSettings settings)
    {
        EnsureMigrated(ref settings);
        settings.RuntimeConfiguration ??= TenantConfiguration.CreateDefault();
        var cfg = settings.RuntimeConfiguration;

        NormalizeRuntimeSections(cfg);
        cfg.ConfigurationSchemaVersion = TenantConfigurationSchema.Current;

        return cfg;
    }
}
