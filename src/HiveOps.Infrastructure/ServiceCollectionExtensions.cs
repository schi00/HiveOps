using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using HiveOps.Application.Interfaces;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Cache;
using HiveOps.Infrastructure.Deployment;
using HiveOps.Infrastructure.Git;
using HiveOps.Infrastructure.Messaging;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using StackExchange.Redis;

namespace HiveOps.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHiveOpsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Multi-tenant context (scoped per request) ──────────────────────
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantSessionContextInterceptor>();
        services.AddMemoryCache();
        services.AddScoped<ITenantConfigService, TenantConfigService>();

        // ── EF Core + SQL Server ───────────────────────────────────────────
        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var connStr = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Missing 'DefaultConnection' connection string.");
            var tenantInterceptor = sp.GetRequiredService<TenantSessionContextInterceptor>();
            options.UseSqlServer(connStr, sql =>
            {
                sql.EnableRetryOnFailure(3);
                sql.CommandTimeout(30);
            });
            options.AddInterceptors(tenantInterceptor);
        }, ServiceLifetime.Scoped);

        // ── Redis ──────────────────────────────────────────────────────────
        services.Configure<RedisOptions>(configuration.GetSection(RedisOptions.SectionName));
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var opts = configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>()
                ?? new RedisOptions();
            var config = ConfigurationOptions.Parse(opts.ConnectionString);
            config.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(config);
        });
        services.AddScoped<IConversationStateManager, RedisConversationStateManager>();

        // ── Semantic Kernel ────────────────────────────────────────────────
        services.Configure<SemanticKernelOptions>(configuration.GetSection(SemanticKernelOptions.SectionName));
        services.Configure<QueryNormalizerOptions>(configuration.GetSection(QueryNormalizerOptions.SectionName));
        services.AddSingleton<KernelFactory>();
        services.AddScoped<IEmbeddingService, SemanticKernelEmbeddingService>();
        services.AddHttpClient<IQueryNormalizer, OpenRouterQueryNormalizer>();

        // ── Messaging ──────────────────────────────────────────────────────
        services.AddHttpClient<IMessagingChannel, MetaWhatsAppChannel>();

        // ── Git & Deployment (CI/CD pipeline stubs) ────────────────────────
        services.AddScoped<IGitService, LocalGitService>();
        services.AddScoped<IDeploymentService, PipelineDeploymentService>();

        // ── Tenant Data Fixer ────────────────────────────────────────────────
        services.AddScoped<ITenantDataFixerService, TenantDataFixerService>();

        return services;
    }
}
