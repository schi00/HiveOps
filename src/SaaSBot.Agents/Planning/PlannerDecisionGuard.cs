using SaaSBot.Domain.Enums;

namespace SaaSBot.Agents.Planning;

public static class PlannerDecisionGuard
{
    private static readonly HashSet<string> ValidDecisions =
    [
        PlannerDecisionKinds.ToolCall,
        PlannerDecisionKinds.Respond,
        PlannerDecisionKinds.Escalate
    ];

    public static bool TryValidate(
        PlannerDecision decision,
        AgentPlannerContext context,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;

        if (!ValidDecisions.Contains(decision.Decision))
        {
            rejectionReason = "Decision kind is invalid.";
            return false;
        }

        if (decision.Confidence < 0 || decision.Confidence > 1)
        {
            rejectionReason = "Confidence out of range.";
            return false;
        }

        if (decision.Decision == PlannerDecisionKinds.ToolCall)
        {
            if (string.IsNullOrWhiteSpace(decision.ToolName))
            {
                rejectionReason = "tool_call requires tool_name.";
                return false;
            }

            var tool = context.AvailableTools.FirstOrDefault(t =>
                string.Equals(t.Name, decision.ToolName, StringComparison.OrdinalIgnoreCase));

            if (tool is null)
            {
                rejectionReason = $"Tool '{decision.ToolName}' is not available.";
                return false;
            }

            if (tool.AllowedStates.Count > 0 && !tool.AllowedStates.Contains(context.CurrentState))
            {
                rejectionReason = $"Tool '{tool.Name}' is not allowed in state '{context.CurrentState}'.";
                return false;
            }

            var args = decision.ToolArgs ?? new Dictionary<string, string>();
            foreach (var argument in tool.Arguments.Where(a => a.Value.Required))
            {
                if (!TryGetArgument(args, argument.Key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    rejectionReason = $"Tool '{tool.Name}' requires argument '{argument.Key}'.";
                    return false;
                }
            }

            return true;
        }

        if (decision.Decision == PlannerDecisionKinds.Respond && string.IsNullOrWhiteSpace(decision.Response))
        {
            rejectionReason = "respond requires a non-empty response.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(decision.StateTransition)
            && !Enum.TryParse<ConversationState>(decision.StateTransition, true, out _))
        {
            rejectionReason = "state_transition is not a valid FSM state.";
            return false;
        }

        return true;
    }

    private static bool TryGetArgument(
        IReadOnlyDictionary<string, string> args,
        string requiredArg,
        out string? value)
    {
        var requiredNorm = NormalizeArgKey(requiredArg);
        foreach (var kvp in args)
        {
            if (NormalizeArgKey(kvp.Key) == requiredNorm)
            {
                value = kvp.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string NormalizeArgKey(string key)
    {
        Span<char> buffer = stackalloc char[key.Length];
        var index = 0;
        foreach (var ch in key)
        {
            if (ch == '_')
                continue;

            buffer[index++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..index]);
    }
}
