using Microsoft.EntityFrameworkCore;
using HiveOps.Api.Authentication;
using HiveOps.Api.Utilities;
using HiveOps.Domain.Entities;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Api.Services;

/// <summary>
/// Validates critical security configuration at startup and bootstraps auth data.
/// </summary>
public sealed class AuthBootstrapHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthBootstrapHostedService> _logger;

    public AuthBootstrapHostedService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<AuthBootstrapHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Validate critical security configuration before any database operations
        ValidateSecurityConfiguration();

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

    /// <summary>
    /// Validates critical security configuration and logs warnings for insecure settings.
    /// </summary>
    private void ValidateSecurityConfiguration()
    {
        var warnings = new List<string>();
        var criticalErrors = new List<string>();

        // Check Admin:ApiKey
        var adminApiKey = _configuration["Admin:ApiKey"];
        if (string.IsNullOrWhiteSpace(adminApiKey))
        {
            criticalErrors.Add("Admin:ApiKey is not configured. Admin API endpoints will be inaccessible.");
        }
        else if (adminApiKey.Length < 32)
        {
            warnings.Add($"Admin:ApiKey is only {adminApiKey.Length} characters. Recommended minimum is 32 characters.");
        }
        else if (adminApiKey == "change-me-in-production" || adminApiKey == "admin123" || adminApiKey == "password")
        {
            criticalErrors.Add("Admin:ApiKey uses a default/weak value. Change immediately in production!");
        }

        // Check Git:RepoPath (for code fix deployment)
        var gitRepoPath = _configuration["Git:RepoPath"];
        if (string.IsNullOrWhiteSpace(gitRepoPath))
        {
            warnings.Add("Git:RepoPath is not configured. Code fix deployment features will not work.");
        }

        // Check JWT/Authentication configuration
        var cookieSecure = _configuration["Authentication:Cookie:SecurePolicy"];
        if (string.Equals(cookieSecure, "None", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Authentication cookie SecurePolicy is set to 'None'. Cookies may be transmitted over HTTP.");
        }

        // Log results
        if (criticalErrors.Any())
        {
            _logger.LogError("=== CRITICAL SECURITY ERRORS ===");
            foreach (var error in criticalErrors)
            {
                _logger.LogError("SECURITY: {Error}", error);
            }
            _logger.LogError("Application will continue but may have reduced functionality.");
        }

        if (warnings.Any())
        {
            _logger.LogWarning("=== SECURITY WARNINGS ===");
            foreach (var warning in warnings)
            {
                _logger.LogWarning("SECURITY: {Warning}", warning);
            }
        }

        if (!criticalErrors.Any() && !warnings.Any())
        {
            _logger.LogInformation("Security configuration validation passed.");
        }
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