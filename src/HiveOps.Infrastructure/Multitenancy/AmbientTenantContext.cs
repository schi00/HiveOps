namespace HiveOps.Infrastructure.Multitenancy;

/// <summary>
/// Carries the current tenant across DI scopes created inside the same logical
/// async operation. Background workers process tenant-scoped data inside child
/// scopes; without an ambient value those scopes resolve with no tenant and
/// Postgres RLS hides every row. Set it explicitly around the unit of work and
/// restore the previous value in a finally block:
/// <code>
/// var previous = AmbientTenantContext.CurrentTenantId;
/// AmbientTenantContext.CurrentTenantId = tenantId;
/// try { /* work that may create scopes */ }
/// finally { AmbientTenantContext.CurrentTenantId = previous; }
/// </code>
/// <see cref="TenantContext"/> adopts this value when constructed. Request-time
/// flows are unaffected: the ambient value stays null outside opted-in workers.
/// </summary>
public static class AmbientTenantContext
{
    private static readonly AsyncLocal<Guid?> Current = new();

    public static Guid? CurrentTenantId
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}
