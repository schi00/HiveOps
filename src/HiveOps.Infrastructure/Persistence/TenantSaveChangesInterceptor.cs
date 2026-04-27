using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;

namespace HiveOps.Infrastructure.Persistence;

public sealed class TenantSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<TenantSaveChangesInterceptor> _logger;

    public TenantSaveChangesInterceptor(ITenantContext tenantContext, ILogger<TenantSaveChangesInterceptor> logger)
    {
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ValidateEntries(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ValidateEntries(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void ValidateEntries(DbContext? context)
    {
        if (context is null)
            return;

        var correlationId = (_tenantContext as TenantContext)?.CorrelationId ?? "n/a";

        foreach (var entry in context.ChangeTracker.Entries<ITenantScoped>())
        {
            var entity = entry.Entity;
            var entityType = entity.GetType().Name;
            var state = entry.State;

            if (_tenantContext.IsResolved)
            {
                if (entity.TenantId == Guid.Empty)
                {
                    if (state == EntityState.Deleted)
                    {
                        // Cannot modify a Deleted entity — this is a data integrity issue.
                        _logger.LogError(
                            "Attempted to delete entity {EntityType} with unassigned TenantId. " +
                            "CorrelationId={CorrelationId}",
                            entityType,
                            correlationId);
                        throw new InvalidOperationException(
                            $"Cannot delete entity {entityType} with unassigned TenantId. " +
                            $"CorrelationId={correlationId}.");
                    }

                    entity.TenantId = _tenantContext.TenantId;

                    // Ensure EF tracks the property change for non-Added entries.
                    if (state != EntityState.Added)
                        entry.Property(nameof(ITenantScoped.TenantId)).IsModified = true;

                    _logger.LogDebug(
                        "Auto-assigned TenantId={TenantId} to {EntityType} (State={State}, CorrelationId={CorrelationId})",
                        _tenantContext.TenantId,
                        entityType,
                        state,
                        correlationId);
                }
                else if (entity.TenantId != _tenantContext.TenantId)
                {
                    _logger.LogError(
                        "Cross-tenant data access blocked. Expected={ExpectedTenantId}, Actual={ActualTenantId}, " +
                        "Entity={EntityType}, State={State}, CorrelationId={CorrelationId}, RiskScore=High",
                        _tenantContext.TenantId,
                        entity.TenantId,
                        entityType,
                        state,
                        correlationId);

                    throw new InvalidOperationException(
                        $"Cross-tenant data access blocked. Expected TenantId={_tenantContext.TenantId}, " +
                        $"but entity {entityType} (State={state}) has TenantId={entity.TenantId}. " +
                        $"CorrelationId={correlationId}.");
                }
            }
            else
            {
                if (entity.TenantId == Guid.Empty)
                {
                    _logger.LogWarning(
                        "Tenant context not resolved and entity {EntityType} has empty TenantId. " +
                        "Allowing for seeding/exempt path. CorrelationId={CorrelationId}",
                        entityType,
                        correlationId);
                }
            }
        }
    }
}
