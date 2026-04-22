using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using SaaSBot.Application.Interfaces;

namespace SaaSBot.Agents.Planning;

public sealed class HybridAgentPlanner : IAgentPlanner
{
    private readonly HeuristicPlanner _heuristicPlanner;
    private readonly LlmPlanner _llmPlanner;
    private readonly ITenantConfigService _tenantConfigService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HybridAgentPlanner> _logger;

    public HybridAgentPlanner(
        HeuristicPlanner heuristicPlanner,
        LlmPlanner llmPlanner,
        ITenantConfigService tenantConfigService,
        IConfiguration configuration,
        ILogger<HybridAgentPlanner> logger)
    {
        _heuristicPlanner = heuristicPlanner;
        _llmPlanner = llmPlanner;
        _tenantConfigService = tenantConfigService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<PlannerDecision> PlanNextStepAsync(
        Kernel kernel,
        AgentPlannerContext context,
        CancellationToken cancellationToken = default)
    {
        var heuristic = await _heuristicPlanner.PlanNextStepAsync(kernel, context, cancellationToken);
        if (heuristic.Confidence >= 0.85)
        {
            _logger.LogInformation("Planner selected heuristic decision: {Decision} ({Confidence:0.00})", heuristic.Decision, heuristic.Confidence);
            return heuristic;
        }

        var tenantConfig = await _tenantConfigService.GetConfigurationAsync(context.TenantId, cancellationToken);
        var enableLlm = tenantConfig.Agent.EnableLlmPlanner && _configuration.GetValue<bool>("AgentPlanner:EnableLlm", true);
        if (!enableLlm)
        {
            _logger.LogDebug("AgentPlanner:EnableLlm disabled. Returning heuristic fallback.");
            return heuristic;
        }

        var llm = await _llmPlanner.PlanNextStepAsync(kernel, context, cancellationToken);
        _logger.LogInformation("Planner selected LLM decision: {Decision} ({Confidence:0.00})", llm.Decision, llm.Confidence);
        return llm;
    }
}
