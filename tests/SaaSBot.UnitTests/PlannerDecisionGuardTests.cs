using FluentAssertions;
using SaaSBot.Agents.Planning;
using SaaSBot.Domain.Enums;

namespace SaaSBot.UnitTests;

public sealed class PlannerDecisionGuardTests
{
    [Fact]
    public void TryValidate_Should_Reject_Unknown_Tool()
    {
        var context = BuildContext();
        var decision = new PlannerDecision
        {
            Decision = PlannerDecisionKinds.ToolCall,
            Confidence = 0.9,
            Reasoning = "test",
            ToolName = "non_existing_tool",
            ToolArgs = new Dictionary<string, string>()
        };

        var ok = PlannerDecisionGuard.TryValidate(decision, context, out var reason);

        ok.Should().BeFalse();
        reason.Should().Contain("not available");
    }

    [Fact]
    public void TryValidate_Should_Reject_Missing_Required_Arg()
    {
        var context = BuildContext();
        var decision = new PlannerDecision
        {
            Decision = PlannerDecisionKinds.ToolCall,
            Confidence = 0.9,
            Reasoning = "test",
            ToolName = "search_products",
            ToolArgs = new Dictionary<string, string>()
        };

        var ok = PlannerDecisionGuard.TryValidate(decision, context, out var reason);

        ok.Should().BeFalse();
        reason.Should().Contain("requires argument");
    }

    [Fact]
    public void TryValidate_Should_Accept_Valid_Respond_Decision()
    {
        var context = BuildContext();
        var decision = PlannerDecision.CreateRespond("respuesta", 0.8, "ok");

        var ok = PlannerDecisionGuard.TryValidate(decision, context, out var reason);

        ok.Should().BeTrue();
        reason.Should().BeEmpty();
    }

    private static AgentPlannerContext BuildContext()
    {
        return new AgentPlannerContext
        {
            TenantId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            UserMessage = "hola",
            CurrentState = ConversationState.Idle,
            FlowData = new Dictionary<string, string>(),
            AvailableTools =
            [
                new AgentToolDefinition
                {
                    Name = "search_products",
                    Description = "Search",
                    Arguments = new Dictionary<string, AgentToolArgumentDefinition>
                    {
                        ["query"] = new()
                        {
                            Type = "string",
                            Required = true,
                            Description = "Search query",
                            Example = "nike"
                        }
                    },
                    AllowedStates = [ConversationState.Idle]
                }
            ]
        };
    }
}
