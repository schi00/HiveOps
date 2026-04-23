using HiveOps.Application.Models;

namespace HiveOps.Application.Interfaces;

public interface ICatalogMirrorSyncService
{
    Task<CatalogSyncResult> SyncTenantAsync(Guid tenantId, CatalogSyncSettings settings, CancellationToken ct = default);
}
