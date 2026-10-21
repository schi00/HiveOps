using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using HiveOps.Domain.Interfaces;

namespace HiveOps.Infrastructure.Persistence;

/// <summary>
/// Writes TenantId into the PostgreSQL session GUC <c>app.tenant_id</c> so
/// DB-level Row-Level Security policies (which read
/// <c>current_setting('app.tenant_id', true)</c>) can enforce tenant isolation
/// even if application query filters are bypassed.
///
/// RLS Setup Requirements:
/// 1. Policies on tenant-scoped tables must compare
///    <c>TenantId = current_setting('app.tenant_id', true)::uuid</c>
/// 2. When the tenant is unresolved, the GUC stays unset and
///    <c>current_setting(..., true)</c> returns NULL — the policies then filter
///    out every row (secure default).
/// 3. SuperAdmin bypass should check user role, not just TenantId.
/// </summary>
public sealed class TenantSessionContextInterceptor : DbConnectionInterceptor
{
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<TenantSessionContextInterceptor> _logger;

    public TenantSessionContextInterceptor(ITenantContext tenantContext, ILogger<TenantSessionContextInterceptor> logger)
    {
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);

        // Skip GUC setup if tenant is not resolved — RLS will filter all rows
        // for this connection (secure default).
        if (!_tenantContext.IsResolved)
        {
            _logger.LogDebug("Tenant context not resolved - RLS will filter all rows for this connection");
            return;
        }

        try
        {
            await using var cmd = connection.CreateCommand();
            // set_config(name, value, is_local=false) applies for the whole
            // session, mirroring sp_set_session_context semantics.
            cmd.CommandText = "SELECT set_config('app.tenant_id', @tenantId, false);";

            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "@tenantId";
            parameter.Value = _tenantContext.TenantId.ToString("D");
            cmd.Parameters.Add(parameter);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogDebug("RLS app.tenant_id set for TenantId: {TenantId}", _tenantContext.TenantId);
        }
        catch (Exception ex)
        {
            // Log error but don't fail - RLS policies will still be active
            // and will filter based on NULL TenantId (blocking all access)
            _logger.LogError(ex, "Failed to set RLS app.tenant_id for TenantId: {TenantId}. RLS will block access.", _tenantContext.TenantId);
        }
    }
}
