namespace HiveOps.Domain.Interfaces;

public interface IDynamicConnectionStringResolver
{
    string Resolve(Guid tenantId);
    Task WarmCacheAsync(Guid tenantId, CancellationToken cancellationToken = default);
    void Invalidate(Guid tenantId);
}
