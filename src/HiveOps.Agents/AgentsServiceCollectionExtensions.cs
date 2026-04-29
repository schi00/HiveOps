using MediatR;
using Microsoft.Extensions.DependencyInjection;
using HiveOps.Agents.Orchestration;
using HiveOps.Agents.Planning;
using HiveOps.Agents.Router;
using HiveOps.Agents.Supervision;
using HiveOps.Agents.Support;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Git;
using HiveOps.Infrastructure.Deployment;

namespace HiveOps.Agents;

public static class AgentsServiceCollectionExtensions
{
    public static IServiceCollection AddHiveOpsAgents(this IServiceCollection services)
    {
        // ── Agent Plugins (scoped so they get scoped AppDbContext) ─────────
        services.AddScoped<RouterPlugin>();
        services.AddScoped<SupervisionPlugin>();
        services.AddScoped<AgentRulesEngine>();
        services.AddScoped<SupportPlugin>();
        services.AddScoped<SelfSupportPlugin>();

        // ── Planner runtime (hybrid heuristics + optional LLM) ───────────
        services.AddScoped<HeuristicPlanner>();
        services.AddScoped<LlmPlanner>();
        services.AddScoped<IToolResolver, ToolResolver>();
        services.AddScoped<IPromptBuilder, PromptBuilder>();
        services.AddScoped<IAgentPlanner, HybridAgentPlanner>();
        services.AddScoped<IAgentRuntime, AgentRuntime>();

        // ── Support engine (incident triage) ───────────────────────────────
        services.AddScoped<IncidentAnalysisEngine>();

        // ── MediatR — scan this assembly for handlers ──────────────────────
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(IncomingMessageHandler).Assembly));

        return services;
    }
}
