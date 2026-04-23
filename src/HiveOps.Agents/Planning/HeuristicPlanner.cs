using HiveOps.Domain.Enums;
using Microsoft.SemanticKernel;

namespace HiveOps.Agents.Planning;

public sealed class HeuristicPlanner : IAgentPlanner
{
    public Task<PlannerDecision> PlanNextStepAsync(
        Kernel kernel,
        AgentPlannerContext context,
        CancellationToken cancellationToken = default)
    {
        var userMessage = context.UserMessage ?? string.Empty;
        var text = userMessage.Trim().ToLowerInvariant();

        if (ContainsAny(text, "humano", "asesor", "agente", "persona", "operador"))
        {
            return Task.FromResult(new PlannerDecision
            {
                Decision = PlannerDecisionKinds.Escalate,
                Confidence = 0.97,
                Reasoning = "User explicitly requested human handoff.",
                Response = null,
                ToolName = null,
                ToolArgs = null,
                StateTransition = ConversationState.AwaitingHuman.ToString()
            });
        }

        return Task.FromResult(PlannerDecision.CreateRespond(
            response: string.Empty,
            confidence: 0.20,
            reasoning: "No deterministic high-confidence route; defer to LLM or deterministic fallback."));
    }

    private static bool ContainsAny(string text, params string[] tokens)
        => tokens.Any(text.Contains);
}
