using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SaaSBot.Api.Authentication;
using SaaSBot.Infrastructure.Multitenancy;
using SaaSBot.Infrastructure.Persistence;

namespace SaaSBot.Api.Middleware;

/// <summary>
/// Resolves TenantId from the incoming request before any handler executes.
/// Resolution strategy (in order of precedence):
///   1. X-Api-Key header  → lookup Tenant by ApiKey
///   2. X-WhatsApp-Number header → lookup Tenant by WhatsAppNumber
///   3. 401 if neither can be resolved (except exempted paths)
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private static readonly HashSet<string> _exemptPaths =
    [
        "/health",
        "/swagger",
        "/api/auth",
        "/webhook/whatsapp",
        "/webhooks/whatsapp", // WhatsApp resolves tenant from payload.To inside controller
        "/api/admin", // Protected separately with X-Admin-Key in admin endpoints
        "/dashboard" // Static dashboard shell; data endpoints still enforce auth headers
    ];

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db, TenantContext tenantContext)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (_exemptPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true &&
            context.User.IsInRole(AppRoles.Tenant) &&
            Guid.TryParse(context.User.FindFirstValue(AppClaimTypes.TenantId), out var tenantIdFromClaim))
        {
            tenantContext.SetTenant(tenantIdFromClaim);
            await _next(context);
            return;
        }

        // 1. Try X-Api-Key header
        if (context.Request.Headers.TryGetValue("X-Api-Key", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
        {
            var tenant = await db.Tenants
                .AsNoTracking()
                .Where(t => t.ApiKey == apiKey.ToString() && t.IsActive)
                .Select(t => new { t.Id })
                .FirstOrDefaultAsync(context.RequestAborted);

            if (tenant is null)
            {
                _logger.LogWarning("Invalid or inactive API key from {IP}", context.Connection.RemoteIpAddress);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid or inactive API key.");
                return;
            }

            tenantContext.SetTenant(tenant.Id);
            await _next(context);
            return;
        }

        // 2. Try X-WhatsApp-Number header
        if (context.Request.Headers.TryGetValue("X-WhatsApp-Number", out var phoneNumber) && !string.IsNullOrWhiteSpace(phoneNumber))
        {
            var tenant = await db.Tenants
                .AsNoTracking()
                .Where(t => t.WhatsAppNumber == phoneNumber.ToString() && t.IsActive)
                .Select(t => new { t.Id })
                .FirstOrDefaultAsync(context.RequestAborted);

            if (tenant is not null)
            {
                tenantContext.SetTenant(tenant.Id);
                await _next(context);
                return;
            }
        }

        _logger.LogWarning("Request from {IP} rejected: no valid tenant credentials", context.Connection.RemoteIpAddress);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Tenant credentials required.");
    }
}
