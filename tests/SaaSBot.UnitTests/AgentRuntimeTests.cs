using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using SaaSBot.Agents.Planning;
using SaaSBot.Application.Configuration;
using SaaSBot.Application.Interfaces;
using SaaSBot.Domain.Enums;

namespace SaaSBot.UnitTests;

public sealed class AgentRuntimeTests
{
    [Fact]
    public async Task RunAsync_Should_Return_NotHandled_When_Disabled()
    {
        var planner = new StaticPlanner(PlannerDecision.CreateRespond("ok", 1.0, "test"));
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AgentPlanner:Enabled"] = "false"
        });

        var sut = new AgentRuntime(planner, config, new StubTenantConfigService(), NullLogger<AgentRuntime>.Instance);
        var context = BuildContext();
        var kernel = Kernel.CreateBuilder().Build();

        var result = await sut.RunAsync(
            kernel,
            context,
            (_, _, _) => Task.FromResult(new PlannerToolExecutionResult { Success = true, Output = "ignored" }));

        result.Handled.Should().BeFalse();
        result.Reasoning.Should().Contain("disabled");
    }

    [Fact]
    public async Task RunAsync_Should_Execute_Tool_Then_Respond()
    {
        var decisions = new Queue<PlannerDecision>(
        [
            new PlannerDecision
            {
                Decision = PlannerDecisionKinds.ToolCall,
                Confidence = 0.95,
                Reasoning = "buscar",
                ToolName = "search_products",
                ToolArgs = new Dictionary<string, string> { ["query"] = "nike" }
            },
            PlannerDecision.CreateRespond("Te muestro opciones", 0.9, "done")
        ]);

        var planner = new QueuePlanner(decisions);
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AgentPlanner:Enabled"] = "true",
            ["AgentPlanner:MaxSteps"] = "3"
        });

        var sut = new AgentRuntime(planner, config, new StubTenantConfigService(), NullLogger<AgentRuntime>.Instance);
        var context = BuildContext();
        var kernel = Kernel.CreateBuilder().Build();

        var toolCalls = 0;
        var result = await sut.RunAsync(
            kernel,
            context,
            (toolName, args, _) =>
            {
                toolCalls++;
                toolName.Should().Be("search_products");
                args["query"].Should().Be("nike");
                return Task.FromResult(new PlannerToolExecutionResult
                {
                    Success = true,
                    Output = "resultado de busqueda",
                    StateTransition = ConversationState.InInventoryQuery.ToString()
                });
            });

        toolCalls.Should().Be(1);
        result.Handled.Should().BeTrue();
        result.Escalate.Should().BeFalse();
        result.Response.Should().Be("Te muestro opciones");
        context.CurrentState.Should().Be(ConversationState.InInventoryQuery);
        context.PreviousSteps.Should().HaveCount(1);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static AgentPlannerContext BuildContext()
    {
        return new AgentPlannerContext
        {
            TenantId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            UserMessage = "mostrame nike",
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
                    AllowedStates = [ConversationState.Idle, ConversationState.InInventoryQuery]
                }
            ]
        };
    }

    private sealed class StaticPlanner : IAgentPlanner
    {
        private readonly PlannerDecision _decision;

        public StaticPlanner(PlannerDecision decision)
        {
            _decision = decision;
        }

        public Task<PlannerDecision> PlanNextStepAsync(Kernel kernel, AgentPlannerContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(_decision);
    }

    private sealed class QueuePlanner : IAgentPlanner
    {
        private readonly Queue<PlannerDecision> _decisions;

        public QueuePlanner(Queue<PlannerDecision> decisions)
        {
            _decisions = decisions;
        }

        public Task<PlannerDecision> PlanNextStepAsync(Kernel kernel, AgentPlannerContext context, CancellationToken cancellationToken = default)
        {
            if (_decisions.Count == 0)
                return Task.FromResult(PlannerDecision.CreateRespond("fallback", 0.7, "no decisions left"));

            return Task.FromResult(_decisions.Dequeue());
        }
    }

    private sealed class StubTenantConfigService : ITenantConfigService
    {
        private static readonly TenantConfiguration Default = TenantConfiguration.CreateDefault();

        public Task<TenantConfiguration> GetConfigurationAsync(Guid tenantId, CancellationToken ct = default)
            => Task.FromResult(Default);

        public Task<TenantConfiguration> UpsertConfigurationAsync(Guid tenantId, TenantConfiguration configuration, CancellationToken ct = default)
            => Task.FromResult(configuration);

        public Task<TenantConfiguration> PatchAgentConfigAsync(Guid tenantId, AgentConfig patch, CancellationToken ct = default)
        {
            Default.Agent = patch;
            return Task.FromResult(Default);
        }

        public Task<TenantConfiguration> PatchToolConfigAsync(Guid tenantId, ToolConfig patch, CancellationToken ct = default)
        {
            Default.Tools = patch;
            return Task.FromResult(Default);
        }

        public Task<TenantConfiguration> PatchBusinessConfigAsync(Guid tenantId, BusinessConfig patch, CancellationToken ct = default)
        {
            Default.Business = patch;
            return Task.FromResult(Default);
        }

        public Task InvalidateAsync(Guid tenantId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
