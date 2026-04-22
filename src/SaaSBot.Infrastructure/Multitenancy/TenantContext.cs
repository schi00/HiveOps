using SaaSBot.Domain.Interfaces;

namespace SaaSBot.Infrastructure.Multitenancy;

public sealed class TenantContext : ITenantContext
{
    private Guid _tenantId;
    private bool _isResolved;

    public Guid TenantId => _tenantId;
    public bool IsResolved => _isResolved;

    public void SetTenant(Guid tenantId)
    {
        _tenantId = tenantId;
        _isResolved = true;
    }
}
