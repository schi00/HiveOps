using HiveOps.Application.Models;

namespace HiveOps.Application.Interfaces;

/// <summary>Structured auditing for tenant configuration changes.</summary>
public interface ITenantConfigurationAuditLogger
{
    void Record(TenantConfigurationAuditPayload payload);
}
