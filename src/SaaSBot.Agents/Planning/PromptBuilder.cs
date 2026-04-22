using SaaSBot.Application.Interfaces;

namespace SaaSBot.Agents.Planning;

public interface IPromptBuilder
{
    Task<string> BuildPlannerPromptAsync(AgentPlannerContext context, CancellationToken ct = default);
}

public sealed class PromptBuilder : IPromptBuilder
{
    private readonly ITenantConfigService _tenantConfigService;

    public PromptBuilder(ITenantConfigService tenantConfigService)
    {
        _tenantConfigService = tenantConfigService;
    }

    public async Task<string> BuildPlannerPromptAsync(AgentPlannerContext context, CancellationToken ct = default)
    {
        var config = await _tenantConfigService.GetConfigurationAsync(context.TenantId, ct);
        var basePrompt = PlannerPromptTemplate.Build(context);

        var tone = config.Agent.Tone;
        var customPrompt = config.Agent.SystemPromptOverride;
        var variables = config.Agent.PromptVariables;
        var businessName = config.Business.Name ?? "nuestro local";
        var businessDescription = config.Business.Description ?? "";
        var toneProfile = config.Business.ToneProfile ?? "friendly";

        var header = string.IsNullOrWhiteSpace(customPrompt)
            ? $"""
            You are a conversational agent for {businessName}.
            {(string.IsNullOrWhiteSpace(businessDescription) ? "" : $"Business context: {businessDescription}\n")}Conversational tone: {toneProfile} (natural, not robotic)
            Company tone preference: {tone}
            Goal: Help customers with purchases and information naturally.

            """
            : $"""
            Tenant system prompt override:
            {customPrompt}

            Business name: {businessName}
            {(string.IsNullOrWhiteSpace(businessDescription) ? "" : $"Business context: {businessDescription}\n")}Conversational tone: {toneProfile}
            Tenant tone: {tone}

            """;

        if (variables.Count == 0)
            return header + "\n" + basePrompt;

        var variableLines = string.Join("\n", variables.Select(v => $"- {v.Key}: {v.Value}"));
        return $"{header}\nTenant prompt variables:\n{variableLines}\n\n{basePrompt}";
    }
}
