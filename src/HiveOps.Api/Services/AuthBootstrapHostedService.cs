using Microsoft.EntityFrameworkCore;
using HiveOps.Api.Authentication;
using HiveOps.Api.Utilities;
using HiveOps.Domain.Entities;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Api.Services;

public sealed class AuthBootstrapHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AuthBootstrapHostedService> _logger;

    public AuthBootstrapHostedService(IServiceProvider serviceProvider, ILogger<AuthBootstrapHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var industrySettings = scope.ServiceProvider.GetRequiredService<IndustrySettingsService>();

        if (!await db.Database.CanConnectAsync(cancellationToken))
            return;

        await EnsureAdminUserAsync(db, cancellationToken);
        await EnsureTenantUsersAndDefaultsAsync(db, industrySettings, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Auth bootstrap completed.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task EnsureAdminUserAsync(AppDbContext db, CancellationToken ct)
    {
        var admin = await db.AppUsers.FirstOrDefaultAsync(u => u.Username == "admin00", ct);
        if (admin is null)
        {
            db.AppUsers.Add(new AppUser
            {
                Username = "admin00",
                Email = "admin00@HiveOps.local",
                Role = AppRoles.Admin,
                PasswordHash = PasswordSecurity.HashPassword("123456"),
                IsActive = true,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            return;
        }

        admin.Role = AppRoles.Admin;
        admin.IsActive = true;
        admin.UpdatedAt = DateTimeOffset.UtcNow;
        if (!PasswordSecurity.VerifyPassword("123456", admin.PasswordHash))
            admin.PasswordHash = PasswordSecurity.HashPassword("123456");
    }

    private static async Task EnsureTenantUsersAndDefaultsAsync(AppDbContext db, IndustrySettingsService industrySettings, CancellationToken ct)
    {
        var tenants = await db.Tenants.ToListAsync(ct);

        foreach (var tenant in tenants)
        {
            tenant.Industry ??= InferIndustry(tenant.Name, null);

            var username = $"tenant_{Slug(tenant.Name)}";
            var existingUser = await db.AppUsers.FirstOrDefaultAsync(u => u.Username == username, ct);
            if (existingUser is null)
            {
                db.AppUsers.Add(new AppUser
                {
                    Username = username,
                    Email = $"{username}@HiveOps.local",
                    Role = AppRoles.Tenant,
                    TenantId = tenant.Id,
                    PasswordHash = PasswordSecurity.HashPassword("123456"),
                    IsActive = true,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }

            var current = TenantSettingsJson.Parse(tenant.ConfigJson);
            var defaults = industrySettings.BuildDefaults(tenant.Industry);
            tenant.ConfigJson = TenantSettingsJson.Stringify(industrySettings.MergeMissingValues(current, defaults));
        }
    }

    private static string InferIndustry(string? tenantName, string? topCategory)
    {
        var source = $"{tenantName} {topCategory}".ToLowerInvariant();
        if (source.Contains("sport") || source.Contains("running") || source.Contains("deporte")) return "deportes";
        if (source.Contains("urban") || source.Contains("denim") || source.Contains("moda")) return "moda";
        if (source.Contains("outdoor") || source.Contains("trek") || source.Contains("monta")) return "outdoor";
        return "retail";
    }

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch == ' ').ToArray();
        return new string(chars).Replace(' ', '_');
    }
}