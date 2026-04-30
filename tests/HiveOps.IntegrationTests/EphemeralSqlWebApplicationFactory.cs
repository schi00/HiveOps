using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using HiveOps.Api.Services;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.IntegrationTests;

/// <summary>
/// Runs the API against a real SQL Server + Redis (CI ephemeral stack). Requires env
/// <c>HIVEOPS_EPHEMERAL_SQL</c> and <c>HIVEOPS_EPHEMERAL_REDIS</c>. Database must already exist (run db/bootstrap).
/// </summary>
public sealed class EphemeralSqlWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var sql = Environment.GetEnvironmentVariable("HIVEOPS_EPHEMERAL_SQL")
            ?? throw new InvalidOperationException("HIVEOPS_EPHEMERAL_SQL is not set.");
        var redis = Environment.GetEnvironmentVariable("HIVEOPS_EPHEMERAL_REDIS") ?? "localhost:6379";

        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var adminKey = Environment.GetEnvironmentVariable("HIVEOPS_EPHEMERAL_ADMIN_KEY");
            if (string.IsNullOrWhiteSpace(adminKey))
                adminKey = "ephemeral-smoke-admin-key-32chars-min!";

            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = sql,
                ["Redis:ConnectionString"] = redis,
                ["Git:RepoPath"] = Path.GetTempPath(),
                ["Admin:ApiKey"] = adminKey,
                ["HiveOps:DataProtectionKeysPath"] = Path.Combine(Path.GetTempPath(), "hiveops-ephemeral-dp", Guid.NewGuid().ToString("N")),
                ["HiveOps:Deployment:Mode"] = nameof(HiveOpsDeploymentMode.Ephemeral),
                ["HiveOps:Deployment:Billing"] = nameof(HiveOpsBillingMode.None),
                ["HiveOps:Deployment:AllowInsecureJwtForTests"] = "true",
                ["HiveOps:Deployment:AllowEmptyAdminApiKeyInEphemeral"] = "false",
                ["HiveOps:Deployment:AllowInMemoryConversationState"] = "false",
                ["Jwt:Key"] = HiveOpsSecurityDefaults.InsecureDevelopmentJwtKey
            });
        });

        builder.ConfigureServices(services =>
        {
            var dbDescriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                         || d.ServiceType == typeof(AppDbContext)
                         || (d.ServiceType.FullName?.Contains("DbContextOptions", StringComparison.Ordinal) == true))
                .ToList();
            foreach (var d in dbDescriptors)
                services.Remove(d);

            services.RemoveAll(typeof(IEmailService));
            services.RemoveAll(typeof(IWhatsAppAccessTokenValidator));
            services.RemoveAll(typeof(IMessagingChannel));
            services.AddSingleton<IEmailService, FakeEmailService>();
            services.AddSingleton<IWhatsAppAccessTokenValidator, FakeWhatsAppAccessTokenValidator>();
            services.AddSingleton<IMessagingChannel, FakeMessagingChannel>();

            services.RemoveAll(typeof(IGitService));
            services.AddSingleton<IGitService, FakeGitService>();

            services.AddDbContext<AppDbContext>((sp, options) =>
            {
                options.UseSqlServer(sql, sqlOpts =>
                {
                    sqlOpts.EnableRetryOnFailure(3);
                    sqlOpts.CommandTimeout(60);
                });
                var tenantInterceptor = sp.GetRequiredService<TenantSessionContextInterceptor>();
                var saveChangesInterceptor = sp.GetRequiredService<TenantSaveChangesInterceptor>();
                options.AddInterceptors(tenantInterceptor, saveChangesInterceptor);
            });
        });
    }
}
