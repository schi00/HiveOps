namespace HiveOps.Domain.Interfaces;

public interface ITenantLookupService
{
    Task<Guid?> FindByIdAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<Guid?> FindByApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);
    Task<Guid?> FindByWhatsAppNumberAsync(string phoneNumber, CancellationToken cancellationToken = default);
}
