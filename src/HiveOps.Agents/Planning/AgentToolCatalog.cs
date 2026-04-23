using HiveOps.Domain.Enums;

namespace HiveOps.Agents.Planning;

public static class AgentToolCatalog
{
    public static IReadOnlyList<AgentToolDefinition> GetDefaultDefinitions()
    {
        return
        [
            new AgentToolDefinition
            {
                Name = "escalate_to_human",
                Description = "Requests human handoff when automation should stop due to frustration, explicit request, or out-of-scope need.",
                WhenToUse =
                [
                    "User explicitly asks for human support.",
                    "Repeated misunderstandings or frustration signals.",
                    "Out-of-scope request for automated flow."
                ],
                WhenNotToUse =
                [
                    "A valid domain tool can still solve user intent safely.",
                    "Single low-confidence turn where clarification is enough."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["reason"] = new() { Type = "string", Required = true, Description = "Operational reason for escalation.", Example = "User explicitly requested human support" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "Quiero hablar con una persona",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["reason"] = "Explicit human handoff request"
                        }
                    }
                ],
                AllowedStates = [ConversationState.Idle, ConversationState.InSupport, ConversationState.AwaitingHuman]
            },
            new AgentToolDefinition
            {
                Name = "analyze_incident",
                Description = "Analyzes a reported incident, classifies it, and asks for missing information if needed.",
                WhenToUse =
                [
                    "User reports a bug, error, database issue, or support incident.",
                    "User describes a technical problem with the system."
                ],
                WhenNotToUse =
                [
                    "User asks for product inventory or commercial info.",
                    "User is in a purchase or reservation flow."
                ],
                Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                {
                    ["conversation_id"] = new() { Type = "string", Required = true, Description = "Conversation ID.", Example = "a3f5c7e1-9d2b-4a0f-8df3-4f1cd64b21ab" },
                    ["description"] = new() { Type = "string", Required = true, Description = "User's incident description.", Example = "Messages are being duplicated" }
                },
                Examples =
                [
                    new AgentToolUsageExample
                    {
                        UserMessage = "Hay un bug en el bot",
                        ToolArgs = new Dictionary<string, string>
                        {
                            ["conversation_id"] = "current_conversation_id",
                            ["description"] = "Hay un bug en el bot"
                        }
                    }
                ],
                AllowedStates = [ConversationState.Idle, ConversationState.InSupport, ConversationState.AwaitingHuman]
            }
        ];
    }
}
