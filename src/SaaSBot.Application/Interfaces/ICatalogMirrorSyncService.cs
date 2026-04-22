using SaaSBot.Application.Models;

namespace SaaSBot.Application.Interfaces;

public interface ICatalogMirrorSyncService
{
    Task<CatalogSyncResult> SyncTenantAsync(Guid tenantId, CatalogSyncSettings settings, CancellationToken ct = default);
}
