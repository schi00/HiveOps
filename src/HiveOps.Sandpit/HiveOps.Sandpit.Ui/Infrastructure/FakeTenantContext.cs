using HiveOps.Domain.Interfaces;

namespace HiveOps.Sandpit.Ui.Infrastructure;

public sealed class FakeTenantContext : ITenantContext
{
    public Guid TenantId { get; } = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public bool IsResolved => true;
}
