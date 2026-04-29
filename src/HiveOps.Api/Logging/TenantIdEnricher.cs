using Serilog.Core;
using Serilog.Events;
using HiveOps.Infrastructure.Multitenancy;

namespace HiveOps.Api.Logging;

/// <summary>
/// Enriches log events with the current TenantId from TenantContext.
/// </summary>
public sealed class TenantIdEnricher : ILogEventEnricher
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TenantIdEnricher(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
            return;

        var tenantContext = httpContext.RequestServices.GetService<TenantContext>();
        if (tenantContext?.IsResolved == true)
        {
            var tenantIdProperty = propertyFactory.CreateProperty("TenantId", tenantContext.TenantId.ToString("D"));
            logEvent.AddPropertyIfAbsent(tenantIdProperty);
        }
        else
        {
            var unresolvedProperty = propertyFactory.CreateProperty("TenantId", "unresolved");
            logEvent.AddPropertyIfAbsent(unresolvedProperty);
        }
    }
}
