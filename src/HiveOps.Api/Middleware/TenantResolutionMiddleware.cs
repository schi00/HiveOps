using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using HiveOps.Api.Authentication;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.Multitenancy;

namespace HiveOps.Api.Middleware;

/// <summary>
/// Resolves TenantId from the incoming request before any handler executes.
/// Resolution strategy (in order of precedence via ITenantProvider pipeline):
///   1. X-Tenant-Id header
///   2. JWT Tenant claim (for Tenant role)
///   3. X-Api-Key header
///   4. X-WhatsApp-Number header
///   5. 401 if none can be resolved (except exempted paths)
///   SuperAdmin/Admin bypass without tenant.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private static readonly HashSet<string> _exemptPaths =
    [
        "/health",
        "/swagger",
        "/api/auth",
        "/webhook/whatsapp",
        "/webhooks/whatsapp",
        "/api/admin",
        "/dashboard",
        "/api/support"
    ];

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ITenantProvider tenantProvider,
        IDynamicConnectionStringResolver connectionStringResolver,
        TenantContext tenantContext)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (_exemptPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        // SuperAdmin/Admin bypass without tenant resolution
        // Controllers will handle IsPrivileged check to allow global data access
        if (context.User.Identity?.IsAuthenticated == true &&
            (context.User.IsInRole(AppRoles.SuperAdmin) || context.User.IsInRole(AppRoles.Admin)))
        {
            await _next(context);
            return;
        }

        context.Request.Headers.TryGetValue("X-Tenant-Id", out var tenantIdHeaderValue);
        context.Request.Headers.TryGetValue("X-Api-Key", out var apiKeyHeaderValue);
        context.Request.Headers.TryGetValue("X-WhatsApp-Number", out var whatsAppHeaderValue);

        var tenantIdClaim = context.User.FindFirstValue(AppClaimTypes.TenantId);
        var userRole = context.User.FindFirstValue(ClaimTypes.Role);

        var result = await tenantProvider.ResolveAsync(
            tenantIdHeaderValue.ToString(),
            apiKeyHeaderValue.ToString(),
            whatsAppHeaderValue.ToString(),
            tenantIdClaim,
            userRole,
            context.RequestAborted);

        if (result.IsResolved)
        {
            tenantContext.SetTenant(result.TenantId!.Value);

            // Warm the connection-string cache so downstream DbContext gets a hot path
            await connectionStringResolver.WarmCacheAsync(result.TenantId.Value, context.RequestAborted);

            // Observability: set correlation id from header or generate one
            if (context.Request.Headers.TryGetValue("X-Correlation-Id", out var correlationId) && !string.IsNullOrWhiteSpace(correlationId))
                tenantContext.CorrelationId = correlationId.ToString();
            else
                tenantContext.CorrelationId = Guid.NewGuid().ToString("N");

            _logger.LogInformation(
                "Tenant resolved. Source={Source}, TenantId={TenantId}, CorrelationId={CorrelationId}, Path={Path}",
                result.Source,
                result.TenantId.Value,
                tenantContext.CorrelationId,
                path);

            await _next(context);
            return;
        }

        _logger.LogWarning(
            "Request from {IP} rejected: no valid tenant credentials. CorrelationId={CorrelationId}, Path={Path}, Error={Error}",
            context.Connection.RemoteIpAddress,
            tenantContext.CorrelationId ?? "n/a",
            path,
            result.ErrorMessage);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync(result.ErrorMessage ?? "Tenant credentials required.");
    }
}
