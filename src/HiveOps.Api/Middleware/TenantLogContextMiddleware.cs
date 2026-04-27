using Microsoft.AspNetCore.Http;
using Serilog.Context;
using HiveOps.Infrastructure.Multitenancy;

namespace HiveOps.Api.Middleware;

/// <summary>
/// Injects tenant observability fields (TenantId, CorrelationId, RiskScore) into the Serilog
/// LogContext so that every log emitted during the request is enriched automatically.
/// This middleware must run AFTER TenantResolutionMiddleware.
/// </summary>
public sealed class TenantLogContextMiddleware
{
    private readonly RequestDelegate _next;

    public TenantLogContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        var tenantId = tenantContext.IsResolved ? tenantContext.TenantId.ToString("D") : "unresolved";
        var correlationId = tenantContext.CorrelationId ?? Guid.NewGuid().ToString("N");
        var riskScore = tenantContext.RiskScore;

        using (LogContext.PushProperty("TenantId", tenantId))
        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("RiskScore", riskScore))
        {
            // Also set CorrelationId on HttpContext.Items for downstream non-Serilog loggers
            context.Items["CorrelationId"] = correlationId;

            await _next(context);
        }
    }
}
