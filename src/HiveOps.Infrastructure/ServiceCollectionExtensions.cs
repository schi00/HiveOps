using Microsoft.AspNetCore.DataProtection;
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
        // ── Data Protection ────────────────────────────────────────────────
        var dataProtectionPath = configuration.GetValue<string>("HiveOps:DataProtectionKeysPath") ?? "keys/dataprotection";
        Directory.CreateDirectory(dataProtectionPath);
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
            .SetApplicationName("HiveOps");

        // ── Multi-tenant context (scoped per request) ──────────────────────
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantSessionContextInterceptor>();
        services.AddScoped<TenantSaveChangesInterceptor>();
        services.AddMemoryCache();
        services.AddScoped<ITenantConfigService, TenantConfigService>();

        // ── Tenant resolution pipeline ─────────────────────────────────────
        services.AddScoped<ITenantLookupService, TenantLookupService>();
        services.AddScoped<ITenantProvider, TenantProvider>();
        services.AddSingleton<IDynamicConnectionStringResolver, DynamicConnectionStringResolver>();

        // ── EF Core + SQL Server ───────────────────────────────────────────
        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var connStr = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Missing 'DefaultConnection' connection string.");
            var tenantInterceptor = sp.GetRequiredService<TenantSessionContextInterceptor>();
            var saveChangesInterceptor = sp.GetRequiredService<TenantSaveChangesInterceptor>();
            options.UseSqlServer(connStr, sql =>
            {
                sql.EnableRetryOnFailure(3);
                sql.CommandTimeout(30);
            });
            options.AddInterceptors(tenantInterceptor, saveChangesInterceptor);
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
        services.AddSingleton<IGitService, LocalGitService>();
        services.AddSingleton<IDeploymentService, PipelineDeploymentService>();

        // ── Tenant Data Fixer ────────────────────────────────────────────────
        services.AddScoped<ITenantDataFixerService, TenantDataFixerService>();

        return services;
    }
}
