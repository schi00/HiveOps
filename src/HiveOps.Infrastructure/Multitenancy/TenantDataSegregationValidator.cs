using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using HiveOps.Domain.Interfaces;

namespace HiveOps.Infrastructure.Multitenancy;

public sealed class TenantDataSegregationValidator
{
    private readonly ILogger<TenantDataSegregationValidator> _logger;

    public TenantDataSegregationValidator(ILogger<TenantDataSegregationValidator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Diagnostic helper that verifies every ITenantScoped entity type has an active
    /// global query filter configured on the given DbContext.
    /// </summary>
    public void ValidateQueryFilterCompliance(DbContext context)
    {
        var tenantScopedTypes = context.Model.GetEntityTypes()
            .Where(e => typeof(ITenantScoped).IsAssignableFrom(e.ClrType))
            .ToList();

        foreach (var entityType in tenantScopedTypes)
        {
            var hasQueryFilter = entityType.GetQueryFilter() != null;
            if (!hasQueryFilter)
            {
                _logger.LogError(
                    "Tenant-scoped entity {EntityType} is missing a global query filter. " +
                    "This is a critical security gap.",
                    entityType.ClrType.Name);
            }
            else
            {
                _logger.LogDebug(
                    "Tenant-scoped entity {EntityType} has global query filter configured.",
                    entityType.ClrType.Name);
            }
        }
    }
}
