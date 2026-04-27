namespace HiveOps.Domain.Interfaces;

public interface ITenantScoped
{
    Guid TenantId { get; set; }
}
