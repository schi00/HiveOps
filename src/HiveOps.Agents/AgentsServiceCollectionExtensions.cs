using MediatR;
using Microsoft.Extensions.DependencyInjection;
using HiveOps.Agents.Commercial;
using HiveOps.Agents.Ingestion;
using HiveOps.Agents.Inventory;
using HiveOps.Agents.Orchestration;
using HiveOps.Agents.Planning;
using HiveOps.Agents.Router;
using HiveOps.Agents.StaticInfo;
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
        services.AddScoped<InventoryPlugin>();
        services.AddScoped<StaticInfoPlugin>();
        services.AddScoped<CommercialPlugin>();
        services.AddScoped<IngestionPlugin>();
        services.AddScoped<SupervisionPlugin>();
        services.AddScoped<AgentRulesEngine>();
        services.AddScoped<SupportPlugin>();
        services.AddScoped<SelfSupportPlugin>();

        // ── Support Infrastructure (singleton or scoped as appropriate) ────
        services.AddSingleton<IGitService, LocalGitService>();
        services.AddSingleton<IDeploymentService, PipelineDeploymentService>();

        // ── Planner runtime (hybrid heuristics + optional LLM) ───────────
        services.AddScoped<HeuristicPlanner>();
        services.AddScoped<LlmPlanner>();
        services.AddScoped<IToolResolver, ToolResolver>();
        services.AddScoped<IPromptBuilder, PromptBuilder>();
        services.AddScoped<IAgentPlanner, HybridAgentPlanner>();
        services.AddScoped<IAgentRuntime, AgentRuntime>();

        // ── MediatR — scan this assembly for handlers ──────────────────────
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(IncomingMessageHandler).Assembly));

        return services;
    }
}
