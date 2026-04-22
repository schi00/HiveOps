using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SaaSBot.Domain.Interfaces;

namespace SaaSBot.Infrastructure.Persistence;

/// <summary>
/// Writes TenantId into SQL Server SESSION_CONTEXT so DB-level Row-Level Security
/// can enforce tenant isolation even if application filters are bypassed.
/// </summary>
public sealed class TenantSessionContextInterceptor : DbConnectionInterceptor
{
    private readonly ITenantContext _tenantContext;

    public TenantSessionContextInterceptor(ITenantContext tenantContext)
    {
        _tenantContext = tenantContext;
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

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId, @read_only=1;";

        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "@tenantId";
        parameter.Value = _tenantContext.IsResolved ? _tenantContext.TenantId : DBNull.Value;
        cmd.Parameters.Add(parameter);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
