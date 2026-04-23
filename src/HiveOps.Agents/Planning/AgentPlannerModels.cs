using System.Text.Json.Serialization;
using HiveOps.Domain.Enums;

namespace HiveOps.Agents.Planning;

public static class PlannerDecisionKinds
{
    public const string ToolCall = "tool_call";
    public const string Respond = "respond";
    public const string Escalate = "escalate";
}

public sealed class AgentToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<string> WhenToUse { get; init; } = [];
    public IReadOnlyList<string> WhenNotToUse { get; init; } = [];
    public IReadOnlyDictionary<string, AgentToolArgumentDefinition> Arguments { get; init; } = new Dictionary<string, AgentToolArgumentDefinition>();
    public IReadOnlyList<AgentToolUsageExample> Examples { get; init; } = [];
    public IReadOnlyList<ConversationState> AllowedStates { get; init; } = [];
}

public sealed class AgentToolArgumentDefinition
{
    public required string Type { get; init; }
    public required bool Required { get; init; }
    public required string Description { get; init; }
    public required string Example { get; init; }
}

public sealed class AgentToolUsageExample
{
    public required string UserMessage { get; init; }
    public required IReadOnlyDictionary<string, string> ToolArgs { get; init; }
}

public sealed class AgentPlannerStep
{
    public required string ToolName { get; init; }
    public required Dictionary<string, string> ToolArgs { get; init; }
    public required string Result { get; init; }
}

public sealed class AgentPlannerContext
{
    public required Guid TenantId { get; init; }
    public required Guid ConversationId { get; init; }
    public required string UserMessage { get; init; }
    public required ConversationState CurrentState { get; set; }
    public required Dictionary<string, string> FlowData { get; init; }
    public string? IntentSignal { get; init; }
    public List<AgentPlannerStep> PreviousSteps { get; } = [];
    public required IReadOnlyList<AgentToolDefinition> AvailableTools { get; init; }
}

public sealed class PlannerDecision
{
    [JsonPropertyName("decision")]
    public string Decision { get; init; } = PlannerDecisionKinds.Respond;

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    [JsonPropertyName("reasoning")]
    public string Reasoning { get; init; } = string.Empty;

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("tool_args")]
    public Dictionary<string, string>? ToolArgs { get; init; }

    [JsonPropertyName("response")]
    public string? Response { get; init; }

    [JsonPropertyName("state_transition")]
    public string? StateTransition { get; init; }

    public static PlannerDecision CreateRespond(string response, double confidence, string reasoning)
        => new()
        {
            Decision = PlannerDecisionKinds.Respond,
            Response = response,
            Confidence = confidence,
            Reasoning = reasoning
        };
}

public sealed class PlannerToolExecutionResult
{
    public required bool Success { get; init; }
    public required string Output { get; init; }
    public string? StateTransition { get; init; }
}

public sealed class AgentRuntimeResult
{
    public required bool Handled { get; init; }
    public required bool Escalate { get; init; }
    public required string? Response { get; init; }
    public required string Reasoning { get; init; }
    public string? StateTransition { get; init; }
}
