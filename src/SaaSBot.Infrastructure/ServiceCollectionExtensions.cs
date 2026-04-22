using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SaaSBot.Application.Interfaces;
using SaaSBot.Domain.Interfaces;
using SaaSBot.Infrastructure.AI;
using SaaSBot.Infrastructure.Cache;
using SaaSBot.Infrastructure.Messaging;
using SaaSBot.Infrastructure.Multitenancy;
using SaaSBot.Infrastructure.Persistence;
using SaaSBot.Infrastructure.Search;
using SaaSBot.Infrastructure.Synchronization;
using StackExchange.Redis;

namespace SaaSBot.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSaaSBotInfrastructure(
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

        // ── Hybrid Search ──────────────────────────────────────────────────
        services.AddScoped<IProductSearchService>(sp =>
        {
            var connStr = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Missing 'DefaultConnection'.");
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqlServerHybridSearchService>>();
            return new SqlServerHybridSearchService(connStr, logger);
        });

        // ── Messaging ──────────────────────────────────────────────────────
        services.AddHttpClient<IMessagingChannel, MetaWhatsAppChannel>();

        // ── External catalog synchronization ───────────────────────────────
        services.AddHttpClient(nameof(CatalogMirrorSyncService));
        services.AddScoped<ICatalogMirrorSyncService, CatalogMirrorSyncService>();

        return services;
    }
}
