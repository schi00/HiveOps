using Serilog.Core;
using Serilog.Events;
using HiveOps.Infrastructure.Multitenancy;

namespace HiveOps.Api.Logging;

/// <summary>
/// Enriches log events with the CorrelationId from TenantContext or generates a new one.
/// </summary>
public sealed class CorrelationIdEnricher : ILogEventEnricher
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CorrelationIdEnricher(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        string correlationId;

        if (httpContext != null)
        {
            // Try to get from TenantContext first
            var tenantContext = httpContext.RequestServices.GetService<TenantContext>();
            correlationId = tenantContext?.CorrelationId 
                ?? httpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault()
                ?? Guid.NewGuid().ToString("N");

            // Store in HttpContext.Items for non-Serilog loggers
            httpContext.Items["CorrelationId"] = correlationId;
        }
        else
        {
            correlationId = Guid.NewGuid().ToString("N");
        }

        var correlationIdProperty = propertyFactory.CreateProperty("CorrelationId", correlationId);
        logEvent.AddPropertyIfAbsent(correlationIdProperty);
    }
}
