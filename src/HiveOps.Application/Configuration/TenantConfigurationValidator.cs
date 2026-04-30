namespace HiveOps.Application.Configuration;

public static class TenantConfigurationValidator
{
    public static IReadOnlyList<string> Validate(TenantConfiguration config)
    {
        var errors = new List<string>();

        if (config.Agent.MaxSteps is < 1 or > 8)
            errors.Add("Agent.MaxSteps must be between 1 and 8.");

        if (config.Business.RecommendationTopK is < 1 or > 20)
            errors.Add("Business.RecommendationTopK must be between 1 and 20.");

        if (config.Channel.WhatsApp.MaxQuickReplyButtons is < 1 or > 3)
            errors.Add("Channel.WhatsApp.MaxQuickReplyButtons must be between 1 and 3.");

        if (config.Escalation.MaxPlannerFailuresBeforeEscalation is < 1 or > 10)
            errors.Add("Escalation.MaxPlannerFailuresBeforeEscalation must be between 1 and 10.");

        foreach (var (toolName, settings) in config.Tools.ToolSettings)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                errors.Add("Tools.ToolSettings contains an empty tool name.");

            if (settings.Priority is < 1 or > 1000)
                errors.Add($"Tools.ToolSettings['{toolName}'].Priority must be between 1 and 1000.");
        }

        var totalTraffic = config.AbTesting.PromptVariants.Sum(v => v.TrafficPercent);
        if (config.AbTesting.Enabled && totalTraffic != 100)
            errors.Add("AbTesting.PromptVariants total TrafficPercent must equal 100 when A/B testing is enabled.");

        foreach (var variant in config.AbTesting.PromptVariants)
        {
            if (string.IsNullOrWhiteSpace(variant.Name))
                errors.Add("AbTesting.PromptVariants contains variant with empty Name.");

            if (variant.TrafficPercent is < 0 or > 100)
                errors.Add($"AbTesting variant '{variant.Name}' has invalid TrafficPercent.");
        }

        foreach (var rule in config.Policies)
        {
            if (string.IsNullOrWhiteSpace(rule.Condition))
                errors.Add("Policies contains rule with empty Condition.");

            if (string.IsNullOrWhiteSpace(rule.Action))
                errors.Add("Policies contains rule with empty Action.");
        }

        if (config.ConfigurationSchemaVersion is < 1 or > TenantConfigurationSchema.MaxKnown)
            errors.Add($"Configuration schema version invalid (received {config.ConfigurationSchemaVersion}).");

        if (config.ConfigurationSchemaVersion > TenantConfigurationSchema.Current)
            errors.Add($"Configuration schema newer than supported (max {TenantConfigurationSchema.Current}).");

        if (string.IsNullOrWhiteSpace(config.Llm.Provider))
            errors.Add("Llm.Provider cannot be empty.");
        if (string.IsNullOrWhiteSpace(config.Llm.Model))
            errors.Add("Llm.Model cannot be empty.");

        if (double.IsNaN(config.Llm.Temperature) || config.Llm.Temperature is < 0 or > 2)
            errors.Add("Llm.Temperature must be between 0 and 2.");

        if (config.Llm.TopP is double topP && (double.IsNaN(topP) || topP is <= 0 or > 1))
            errors.Add("Llm.TopP must be between 0 and 1.");

        if (config.Llm.MaxOutputTokens is < 128 or > 32000)
            errors.Add("Llm.MaxOutputTokens must be between 128 and 32000.");

        if (!string.IsNullOrWhiteSpace(config.DeployGit.GitRepositoryUrl)
            && !Uri.TryCreate(config.DeployGit.GitRepositoryUrl, UriKind.Absolute, out _))
            errors.Add("DeployGit.GitRepositoryUrl must be an absolute URI when supplied.");

        return errors;
    }
}
