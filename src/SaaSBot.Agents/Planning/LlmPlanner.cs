using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace SaaSBot.Agents.Planning;

public sealed class LlmPlanner : IAgentPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<LlmPlanner> _logger;
    private readonly IPromptBuilder _promptBuilder;

    public LlmPlanner(ILogger<LlmPlanner> logger, IPromptBuilder promptBuilder)
    {
        _logger = logger;
        _promptBuilder = promptBuilder;
    }

    public async Task<PlannerDecision> PlanNextStepAsync(
        Kernel kernel,
        AgentPlannerContext context,
        CancellationToken cancellationToken = default)
    {
        var prompt = await _promptBuilder.BuildPlannerPromptAsync(context, cancellationToken);

        try
        {
            var result = await kernel.InvokePromptAsync(prompt, cancellationToken: cancellationToken);
            var raw = result.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return PlannerDecision.CreateRespond(string.Empty, 0.0, "LLM planner returned empty payload.");
            }

            var normalized = NormalizeJson(raw);
            var parsed = JsonSerializer.Deserialize<PlannerDecision>(normalized, JsonOptions);
            if (parsed is null)
            {
                return PlannerDecision.CreateRespond(string.Empty, 0.0, "LLM planner payload could not be parsed.");
            }

            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM planner invocation failed.");
            return PlannerDecision.CreateRespond(string.Empty, 0.0, "LLM planner failed; fallback required.");
        }
    }

    private static string NormalizeJson(string raw)
    {
        var cleaned = raw.Trim();

        if (cleaned.StartsWith("```") && cleaned.EndsWith("```"))
        {
            var firstNewLine = cleaned.IndexOf('\n');
            if (firstNewLine > 0)
            {
                cleaned = cleaned[(firstNewLine + 1)..].Trim();
                if (cleaned.EndsWith("```", StringComparison.Ordinal))
                    cleaned = cleaned[..^3].Trim();
            }
        }

        return cleaned;
    }
}
