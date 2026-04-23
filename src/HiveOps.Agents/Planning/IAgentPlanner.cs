using Microsoft.SemanticKernel;

namespace HiveOps.Agents.Planning;

public interface IAgentPlanner
{
    Task<PlannerDecision> PlanNextStepAsync(
        Kernel kernel,
        AgentPlannerContext context,
        CancellationToken cancellationToken = default);
}

public interface IAgentRuntime
{
    Task<AgentRuntimeResult> RunAsync(
        Kernel kernel,
        AgentPlannerContext context,
        Func<string, Dictionary<string, string>, CancellationToken, Task<PlannerToolExecutionResult>> toolExecutor,
        CancellationToken cancellationToken = default);
}
