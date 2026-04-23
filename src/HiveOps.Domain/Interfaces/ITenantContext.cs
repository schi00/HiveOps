namespace HiveOps.Domain.Interfaces;

public interface ITenantContext
{
    Guid TenantId { get; }
    bool IsResolved { get; }
}
