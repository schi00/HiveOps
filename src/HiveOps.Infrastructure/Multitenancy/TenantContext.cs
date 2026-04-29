using HiveOps.Domain.Interfaces;

namespace HiveOps.Infrastructure.Multitenancy;

public sealed class TenantContext : ITenantContext
{
    private readonly object _lock = new();
    private Guid _tenantId;
    private bool _isResolved;

    public Guid TenantId
    {
        get
        {
            lock (_lock) { return _tenantId; }
        }
    }

    public bool IsResolved
    {
        get
        {
            lock (_lock) { return _isResolved; }
        }
    }

    public string? CorrelationId { get; set; }
    public int RiskScore { get; set; }

    public void SetTenant(Guid tenantId)
    {
        lock (_lock)
        {
            if (_isResolved)
                throw new InvalidOperationException(
                    $"Tenant context is already resolved to TenantId={_tenantId}. " +
                    "Resetting the tenant within the same scope is not allowed to prevent cross-tenant leakage.");

            _tenantId = tenantId;
            _isResolved = true;
        }
    }
}
