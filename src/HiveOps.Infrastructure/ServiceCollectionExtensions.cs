using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HiveOps.Application.Configuration;
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
using HiveOps.Infrastructure.Secrets;
using HiveOps.Infrastructure.Billing;
using HiveOps.Infrastructure.Configuration;

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
        services.AddSingleton<ITenantConfigurationAuditLogger, TenantConfigurationAuditLogger>();

        // ── Secrets (Env -> AWS -> Configuration) ─────────────────────────
        services.Configure<SecretsOptions>(configuration.GetSection(SecretsOptions.SectionName));
        services.AddSingleton<ISecretProvider>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var opts = cfg.GetSection(SecretsOptions.SectionName).Get<SecretsOptions>() ?? new SecretsOptions();
            var providers = new List<ISecretProvider>
            {
                new EnvVarSecretProvider(),
            };
            if (opts.UseAws)
                providers.Add(new AwsSecretsManagerProvider(opts));
            providers.Add(new ConfigurationSecretProvider(cfg));
            return new CompositeSecretProvider(providers.ToArray());
        });

        // ── Tenant resolution pipeline ─────────────────────────────────────
        services.AddScoped<ITenantLookupService, TenantLookupService>();
        services.AddScoped<ITenantProvider, TenantProvider>();
        services.AddSingleton<IDynamicConnectionStringResolver, DynamicConnectionStringResolver>();

        // ── Deployment profile (SaaS / SelfHosted / Ephemeral) ─────────────
        services.Configure<HiveOpsDeploymentOptions>(configuration.GetSection(HiveOpsDeploymentOptions.SectionPath));
        services.AddSingleton<IValidateOptions<HiveOpsDeploymentOptions>, HiveOpsDeploymentOptionsValidator>();
        services.AddOptions<HiveOpsDeploymentOptions>().ValidateOnStart();

        var deployment = configuration.GetSection(HiveOpsDeploymentOptions.SectionPath).Get<HiveOpsDeploymentOptions>()
            ?? new HiveOpsDeploymentOptions();

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

        // ── Redis or in-memory conversation state ──────────────────────────
        if (deployment.Mode == HiveOpsDeploymentMode.Ephemeral && deployment.AllowInMemoryConversationState)
        {
            services.AddSingleton<IConversationStateManager, InMemoryConversationStateManager>();
        }
        else
        {
            services.Configure<RedisOptions>(configuration.GetSection(RedisOptions.SectionName));
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var opts = configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>()
                    ?? new RedisOptions();
                var redisConfig = ConfigurationOptions.Parse(opts.ConnectionString);
                redisConfig.AbortOnConnectFail = false;
                return ConnectionMultiplexer.Connect(redisConfig);
            });
            services.AddScoped<IConversationStateManager, RedisConversationStateManager>();
        }

        // ── Semantic Kernel ────────────────────────────────────────────────
        services.Configure<SemanticKernelOptions>(configuration.GetSection(SemanticKernelOptions.SectionName));
        services.Configure<QueryNormalizerOptions>(configuration.GetSection(QueryNormalizerOptions.SectionName));
        services.AddSingleton<KernelFactory>();
        services.AddScoped<IEmbeddingService, SemanticKernelEmbeddingService>();
        services.AddHttpClient<IQueryNormalizer, OpenRouterQueryNormalizer>();

        // ── Messaging ──────────────────────────────────────────────────────
        services.AddHttpClient<IMessagingChannel, MetaWhatsAppChannel>();

        // ── Git & Deployment (CI/CD pipeline stubs) ────────────────────────
        services.AddSingleton<IGitService, TenantGitService>();
        services.AddSingleton<IDeploymentService, PipelineDeploymentService>();

        // ── Tenant Data Fixer ────────────────────────────────────────────────
        services.AddScoped<ITenantDataFixerService, TenantDataFixerService>();

        // ── Billing ─────────────────────────────────────────────────────────
        services.Configure<StripeOptions>(configuration.GetSection(StripeOptions.SectionName));
        if (deployment.Billing == HiveOpsBillingMode.Stripe)
            services.AddScoped<IStripeService, StripeService>();
        else
            services.AddScoped<IStripeService, DisabledStripeService>();

        return services;
    }
}
