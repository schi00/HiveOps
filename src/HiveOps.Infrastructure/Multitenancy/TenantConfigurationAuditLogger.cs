using Microsoft.Extensions.Logging;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Models;

namespace HiveOps.Infrastructure.Multitenancy;

/// <summary>Writes configuration changes to structured logs (centralized SIEM friendly).
/// </summary>
public sealed class TenantConfigurationAuditLogger : ITenantConfigurationAuditLogger
{
    private readonly ILogger<TenantConfigurationAuditLogger> _logger;

    public TenantConfigurationAuditLogger(ILogger<TenantConfigurationAuditLogger> logger)
    {
        _logger = logger;
    }

    public void Record(TenantConfigurationAuditPayload payload)
    {
        _logger.LogInformation(
            "Tenant configuration changed. TenantId={TenantId} Section={Section} ActorId={ActorId} Message={Message}",
            payload.TenantId,
            payload.Section,
            payload.ActorId,
            payload.Message);
    }
}
