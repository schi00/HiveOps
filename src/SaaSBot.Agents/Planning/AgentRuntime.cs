using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using SaaSBot.Agents.Inventory;
using SaaSBot.Application.Interfaces;
using SaaSBot.Domain.Enums;

namespace SaaSBot.Agents.Planning;

public sealed class AgentRuntime : IAgentRuntime
{
    private readonly IAgentPlanner _planner;
    private readonly IConfiguration _configuration;
    private readonly ITenantConfigService _tenantConfigService;
    private readonly ILogger<AgentRuntime> _logger;

    public AgentRuntime(
        IAgentPlanner planner,
        IConfiguration configuration,
        ITenantConfigService tenantConfigService,
        ILogger<AgentRuntime> logger)
    {
        _planner = planner;
        _configuration = configuration;
        _tenantConfigService = tenantConfigService;
        _logger = logger;
    }

    public async Task<AgentRuntimeResult> RunAsync(
        Kernel kernel,
        AgentPlannerContext context,
        Func<string, Dictionary<string, string>, CancellationToken, Task<PlannerToolExecutionResult>> toolExecutor,
        CancellationToken cancellationToken = default)
    {
        var tenantConfig = await _tenantConfigService.GetConfigurationAsync(context.TenantId, cancellationToken);
        var enabled = tenantConfig.Agent.Enabled && _configuration.GetValue<bool>("AgentPlanner:Enabled", true);
        if (!enabled)
        {
            return new AgentRuntimeResult
            {
                Handled = false,
                Escalate = false,
                Response = null,
                Reasoning = "Agent planner disabled by configuration."
            };
        }

        var globalMaxSteps = _configuration.GetValue<int?>("AgentPlanner:MaxSteps") ?? 5;
        var maxSteps = Math.Clamp(Math.Min(tenantConfig.Agent.MaxSteps, globalMaxSteps), 1, 8);
        string? lastToolOutput = null;
        string? lastStateTransition = null;

        for (var i = 0; i < maxSteps; i++)
        {
            var decision = await _planner.PlanNextStepAsync(kernel, context, cancellationToken);

            _logger.LogInformation(
                "Agent planner decision. Step={Step} Decision={Decision} Confidence={Confidence:0.00} Reason={Reasoning}",
                i + 1,
                decision.Decision,
                decision.Confidence,
                decision.Reasoning);

            if (!PlannerDecisionGuard.TryValidate(decision, context, out var rejectionReason))
            {
                _logger.LogWarning("Planner decision rejected by guardrails: {Reason}", rejectionReason);
                return new AgentRuntimeResult
                {
                    Handled = false,
                    Escalate = false,
                    Response = null,
                    Reasoning = rejectionReason
                };
            }

            if (decision.Decision == PlannerDecisionKinds.Respond)
            {
                return new AgentRuntimeResult
                {
                    Handled = true,
                    Escalate = false,
                    Response = decision.Response,
                    Reasoning = decision.Reasoning,
                    StateTransition = decision.StateTransition
                };
            }

            if (decision.Decision == PlannerDecisionKinds.Escalate)
            {
                return new AgentRuntimeResult
                {
                    Handled = true,
                    Escalate = true,
                    Response = null,
                    Reasoning = decision.Reasoning,
                    StateTransition = decision.StateTransition ?? ConversationState.AwaitingHuman.ToString()
                };
            }

            var args = decision.ToolArgs ?? new Dictionary<string, string>();
            var toolResult = await toolExecutor(decision.ToolName!, args, cancellationToken);
            lastToolOutput = toolResult.Output;
            lastStateTransition = toolResult.StateTransition ?? decision.StateTransition;

            context.PreviousSteps.Add(new AgentPlannerStep
            {
                ToolName = decision.ToolName!,
                ToolArgs = args,
                Result = toolResult.Output
            });

            if (!string.IsNullOrWhiteSpace(lastStateTransition)
                && Enum.TryParse<ConversationState>(lastStateTransition, true, out var parsedState))
            {
                context.CurrentState = parsedState;
            }

            if (!toolResult.Success)
            {
                return new AgentRuntimeResult
                {
                    Handled = false,
                    Escalate = false,
                    Response = null,
                    Reasoning = "Tool execution failed."
                };
            }
        }

        if (!string.IsNullOrWhiteSpace(lastToolOutput))
        {
            var responseText = lastToolOutput;
            if (InventoryPlugin.TryFormatProductResults(lastToolOutput, out var formatted))
                responseText = formatted;

            return new AgentRuntimeResult
            {
                Handled = true,
                Escalate = false,
                Response = responseText,
                Reasoning = "Reached planner step limit; responding with latest tool output.",
                StateTransition = lastStateTransition
            };
        }

        return new AgentRuntimeResult
        {
            Handled = false,
            Escalate = false,
            Response = null,
            Reasoning = "Planner reached max steps without terminal action."
        };
    }
}
