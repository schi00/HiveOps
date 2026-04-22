using SaaSBot.Domain.Enums;
using SaaSBot.Agents.Inventory;
using Microsoft.SemanticKernel;

namespace SaaSBot.Agents.Planning;

public sealed class HeuristicPlanner : IAgentPlanner
{
    public Task<PlannerDecision> PlanNextStepAsync(
        Kernel kernel,
        AgentPlannerContext context,
        CancellationToken cancellationToken = default)
    {
        // If a tool already produced output, return it to avoid repeating the same tool call loop.
        var lastStep = context.PreviousSteps.LastOrDefault();
        if (lastStep is not null
            && string.Equals(lastStep.ToolName, "search_products", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(lastStep.Result))
        {
            var responseText = lastStep.Result;
            if (InventoryPlugin.TryFormatProductResults(lastStep.Result, out var formatted))
                responseText = formatted;

            return Task.FromResult(PlannerDecision.CreateRespond(
                response: responseText,
                confidence: 0.96,
                reasoning: "Reusing latest inventory tool output to avoid repeated identical calls."));
        }

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

        if (context.CurrentState == ConversationState.InOrderFlow && ContainsAny(text, "carrito", "ver carrito"))
        {
            return Task.FromResult(new PlannerDecision
            {
                Decision = PlannerDecisionKinds.ToolCall,
                Confidence = 0.93,
                Reasoning = "Conversation is already in order flow and user asked for cart state.",
                ToolName = "view_cart",
                ToolArgs = new Dictionary<string, string>(),
                Response = null,
                StateTransition = ConversationState.InOrderFlow.ToString()
            });
        }

        if (context.IntentSignal is not null &&
            context.IntentSignal.Equals(IntentType.Inventory.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new PlannerDecision
            {
                Decision = PlannerDecisionKinds.ToolCall,
                Confidence = 0.88,
                Reasoning = "Intent signal indicates inventory query.",
                ToolName = "search_products",
                ToolArgs = new Dictionary<string, string> { ["query"] = userMessage },
                Response = null,
                StateTransition = ConversationState.InInventoryQuery.ToString()
            });
        }

        // Guardrail: route common sports-shopping requests to inventory even if router intent is ambiguous.
        if (ContainsAny(text, "futbol", "fútbol", "football", "footbol", "fulbo", "running", "correr", "zapa", "zapas", "zapatilla", "zapatillas", "basquet", "basket", "basketball",
                "natacion", "nadar", "voley", "voleibol", "yoga", "pilates", "ciclismo", "padel", "boxeo", "trekking", "hiking", "handball", "rugby",
                "cosas para", "articulos para", "articulos de", "algo de", "para practicar", "para jugar")
            || (ContainsAny(text, "jugar", "deporte", "entrenar") && ContainsAny(text, "quiero", "busco", "algo")))
        {
            return Task.FromResult(new PlannerDecision
            {
                Decision = PlannerDecisionKinds.ToolCall,
                Confidence = 0.87,
                Reasoning = "Message looks like product discovery for a sport context.",
                ToolName = "search_products",
                ToolArgs = new Dictionary<string, string> { ["query"] = userMessage },
                Response = null,
                StateTransition = ConversationState.InInventoryQuery.ToString()
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
