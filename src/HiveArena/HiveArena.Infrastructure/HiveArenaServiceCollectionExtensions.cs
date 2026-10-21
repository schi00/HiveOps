using HiveArena.Core;
using HiveArena.Infrastructure.Bridges;
using HiveArena.Infrastructure.Configuration;
using HiveArena.Infrastructure.ProvingGround;
using HiveArena.Infrastructure.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace HiveArena.Infrastructure;

public static class HiveArenaServiceCollectionExtensions
{
    public static IServiceCollection AddHiveArenaInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IScenarioCatalog, ScenarioCatalog>();
        services.AddSingleton<IEphemeralEnvironmentManager, EphemeralEnvironmentManager>();
        services.AddSingleton<IBranchCheckoutService, BranchCheckoutService>();
        services.AddSingleton<IScenarioReplayRunner, ScenarioReplayRunner>();
        services.AddSingleton<ISurvivalReporter, SurvivalReporter>();
        services.AddHttpClient(nameof(HiveOpsIncidentBridge));
        services.AddHttpClient(nameof(HiveOpsValidationCallbackClient));
        services.AddSingleton<IHiveOpsIncidentBridge, HiveOpsIncidentBridge>();
        services.AddSingleton<IHiveOpsValidationCallbackClient, HiveOpsValidationCallbackClient>();
        return services;
    }

    public static IServiceCollection AddHiveArenaConfiguration(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.Configure<HiveArenaBridgeOptions>(configuration.GetSection(HiveArenaBridgeOptions.SectionName));
        services.Configure<HiveOpsCallbackOptions>(configuration.GetSection(HiveOpsCallbackOptions.SectionName));
        services.Configure<ProvingGroundOptions>(configuration.GetSection(ProvingGroundOptions.SectionName));
        return services;
    }
}
