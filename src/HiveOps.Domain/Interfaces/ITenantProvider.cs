using HiveOps.Domain.Models;

namespace HiveOps.Domain.Interfaces;

public interface ITenantProvider
{
    Task<TenantResolutionResult> ResolveAsync(
        string? tenantIdHeader,
        string? apiKeyHeader,
        string? whatsAppNumberHeader,
        string? tenantIdClaim,
        string? userRole,
        CancellationToken cancellationToken = default);
}
