using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using HiveOps.Domain.Interfaces;

namespace HiveOps.Infrastructure.Persistence;

/// <summary>
/// Writes TenantId into SQL Server SESSION_CONTEXT so DB-level Row-Level Security
/// can enforce tenant isolation even if application filters are bypassed.
/// 
/// RLS Setup Requirements:
/// 1. Security predicate function must check SESSION_CONTEXT('TenantId')
/// 2. Security policy must be applied to all tenant-scoped tables
/// 3. SuperAdmin bypass should check user role, not just TenantId
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

    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        await base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
        return result;
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);

        // Skip SESSION_CONTEXT setup if tenant is not resolved
        // This will cause RLS to filter out all rows (secure default)
        if (!_tenantContext.IsResolved)
        {
            _logger.LogDebug("Tenant context not resolved - RLS will filter all rows for this connection");
            return;
        }

        try
        {
            await using var cmd = connection.CreateCommand();
            // Set read_only=1 to prevent tampering within the session
            cmd.CommandText = "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId, @read_only=1;";

            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "@tenantId";
            parameter.Value = _tenantContext.TenantId;
            cmd.Parameters.Add(parameter);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogDebug("RLS SESSION_CONTEXT set for TenantId: {TenantId}", _tenantContext.TenantId);
        }
        catch (Exception ex)
        {
            // Log error but don't fail - RLS policies will still be active
            // and will filter based on NULL TenantId (blocking all access)
            _logger.LogError(ex, "Failed to set RLS SESSION_CONTEXT for TenantId: {TenantId}. RLS will block access.", _tenantContext.TenantId);
        }
    }
}
