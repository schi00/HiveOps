namespace SaaSBot.Application.Configuration;

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

        return errors;
    }
}
