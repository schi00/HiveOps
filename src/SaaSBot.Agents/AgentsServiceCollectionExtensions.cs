using MediatR;
using Microsoft.Extensions.DependencyInjection;
using SaaSBot.Agents.Commercial;
using SaaSBot.Agents.Ingestion;
using SaaSBot.Agents.Inventory;
using SaaSBot.Agents.Orchestration;
using SaaSBot.Agents.Planning;
using SaaSBot.Agents.Router;
using SaaSBot.Agents.StaticInfo;
using SaaSBot.Agents.Supervision;

namespace SaaSBot.Agents;

public static class AgentsServiceCollectionExtensions
{
    public static IServiceCollection AddSaaSBotAgents(this IServiceCollection services)
    {
        // ── Agent Plugins (scoped so they get scoped AppDbContext) ─────────
        services.AddScoped<RouterPlugin>();
        services.AddScoped<InventoryPlugin>();
        services.AddScoped<StaticInfoPlugin>();
        services.AddScoped<CommercialPlugin>();
        services.AddScoped<IngestionPlugin>();
        services.AddScoped<SupervisionPlugin>();
        services.AddScoped<AgentRulesEngine>();

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
