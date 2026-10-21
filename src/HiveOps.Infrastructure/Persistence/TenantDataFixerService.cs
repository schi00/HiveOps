using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HiveOps.Application;
using HiveOps.Application.Configuration;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Infrastructure.Persistence;

public sealed class TenantDataFixerService : ITenantDataFixerService
{
    private readonly AppDbContext _db;
    private readonly TenantContext _tenantContext;
    private readonly IDynamicConnectionStringResolver _connectionStringResolver;
    private readonly IOptions<HiveOpsDeploymentOptions> _deploymentOptions;
    private readonly ILogger<TenantDataFixerService> _logger;

    public TenantDataFixerService(
        AppDbContext db,
        TenantContext tenantContext,
        IDynamicConnectionStringResolver connectionStringResolver,
        IOptions<HiveOpsDeploymentOptions> deploymentOptions,
        ILogger<TenantDataFixerService> logger)
    {
        _db = db;
        _tenantContext = tenantContext;
        _connectionStringResolver = connectionStringResolver;
        _deploymentOptions = deploymentOptions;
        _logger = logger;
    }

    /// <summary>
    /// Executes a validated SQL script against the database that actually hosts the
    /// tenant's business data:
    /// <list type="bullet">
    /// <item><b>Dedicated tenant database</b> (Mode=SelfHosted and the tenant has an
    /// <c>EncryptedConnectionString</c>): a raw <see cref="NpgsqlConnection"/> is opened with the
    /// per-tenant connection string resolved via <see cref="IDynamicConnectionStringResolver"/>.
    /// RLS is not involved — the dedicated database is fully owned by the tenant.</item>
    /// <item><b>Shared catalog database</b> (SaaS/Ephemeral or tenant without a dedicated DB):
    /// the <see cref="AppDbContext"/> connection is used and the transaction is begun through
    /// EF Core (<c>Database.BeginTransactionAsync</c>) so the connection is opened via the EF
    /// pipeline and <see cref="TenantSessionContextInterceptor"/> sets the
    /// <c>app.tenant_id</c> GUC for Row-Level Security. A raw <c>OpenAsync()</c> on the
    /// underlying <see cref="System.Data.Common.DbConnection"/>
    /// would bypass the interceptor and RLS would filter out every row.</item>
    /// </list>
    /// <see cref="DiagnosticLog"/> entries are always written through <see cref="AppDbContext"/>
    /// (the HiveOps catalog), never through the tenant connection.
    /// </summary>
    public async Task<DataFixResult> ExecuteSafeSqlAsync(Guid tenantId, string sqlScript, Guid incidentId, CancellationToken ct = default)
    {
        if (!SafetyValidator.IsSafeSql(sqlScript))
        {
            var reason = SafetyValidator.GetRejectionReason(sqlScript);
            _logger.LogWarning("Tenant {TenantId} | Incident {IncidentId} | SQL rejected: {Reason}", tenantId, incidentId, reason);
            return new DataFixResult(false, $"SQL rejected by safety validator: {reason}");
        }

        // Ensure tenant context is set; a request-scoped caller may already have
        // resolved it (SetTenant is a one-shot guard against cross-tenant leakage).
        if (!_tenantContext.IsResolved)
            _tenantContext.SetTenant(tenantId);
        else if (_tenantContext.TenantId != tenantId)
            throw new InvalidOperationException(
                $"Tenant context is resolved to {_tenantContext.TenantId} but the fix targets {tenantId}.");

        // Tenants has no tenant-scoped query filter; the PK lookup is unambiguous.
        var tenant = await _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);

        var useDedicatedDatabase =
            _deploymentOptions.Value.Mode == HiveOpsDeploymentMode.SelfHosted
            && !string.IsNullOrWhiteSpace(tenant?.EncryptedConnectionString);

        if (useDedicatedDatabase)
            return await ExecuteOnDedicatedDatabaseAsync(tenantId, sqlScript, incidentId, ct);

        return await ExecuteOnCatalogDatabaseAsync(tenantId, sqlScript, incidentId, ct);
    }

    /// <summary>
    /// Shared catalog DB path: the EF-managed transaction guarantees the connection is
    /// opened through the interceptor pipeline (app.tenant_id GUC for RLS) and that the
    /// DiagnosticLog write participates in the same local transaction as the fix.
    /// </summary>
    private async Task<DataFixResult> ExecuteOnCatalogDatabaseAsync(Guid tenantId, string sqlScript, Guid incidentId, CancellationToken ct)
    {
        // EnableRetryOnFailure requires all transactional work to run inside the
        // execution strategy so the whole unit can be retried atomically.
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            try
            {
                var connection = _db.Database.GetDbConnection();
                await using var command = connection.CreateCommand();
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = sqlScript;

                int rowsAffected = await command.ExecuteNonQueryAsync(ct);

                _db.DiagnosticLogs.Add(new DiagnosticLog
                {
                    TenantId = tenantId,
                    IncidentId = incidentId,
                    StepName = "db_fix_executed",
                    Result = $"Executed safe SQL. Rows affected: {rowsAffected}",
                    IsSuccess = true,
                    CreatedAt = DateTimeOffset.UtcNow
                });

                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                _logger.LogInformation("Tenant {TenantId} | Incident {IncidentId} | SQL executed successfully on shared catalog DB. Rows affected: {RowsAffected}", tenantId, incidentId, rowsAffected);
                return new DataFixResult(true, "SQL executed successfully.", rowsAffected);
            }
            catch (Exception ex)
            {
                try { await transaction.RollbackAsync(ct); }
                catch (Exception rbEx) { _logger.LogWarning(rbEx, "Tenant {TenantId} | Incident {IncidentId} | transaction rollback failed.", tenantId, incidentId); }

                await LogFailureAsync(tenantId, incidentId, ex, ct);

                _logger.LogError(ex, "Tenant {TenantId} | Incident {IncidentId} | SQL execution failed.", tenantId, incidentId);
                return new DataFixResult(false, $"Execution failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Dedicated tenant DB path: own <see cref="NpgsqlConnection"/> from the resolved
    /// per-tenant connection string. The fix runs in a transaction on that connection;
    /// diagnostic logging still goes to the catalog via <see cref="AppDbContext"/>.
    /// </summary>
    private async Task<DataFixResult> ExecuteOnDedicatedDatabaseAsync(Guid tenantId, string sqlScript, Guid incidentId, CancellationToken ct)
    {
        string connectionString;
        try
        {
            await _connectionStringResolver.WarmCacheAsync(tenantId, ct);
            connectionString = _connectionStringResolver.Resolve(tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tenant {TenantId} | Incident {IncidentId} | could not resolve dedicated connection string.", tenantId, incidentId);
            await LogFailureAsync(tenantId, incidentId, ex, ct);
            return new DataFixResult(false, $"Tenant database connection unavailable: {ex.Message}");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tenant {TenantId} | Incident {IncidentId} | could not open dedicated tenant DB connection.", tenantId, incidentId);
            await LogFailureAsync(tenantId, incidentId, ex, ct);
            return new DataFixResult(false, $"Tenant database connection failed: {ex.Message}");
        }

        await using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sqlScript;

            int rowsAffected = await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);

            _db.DiagnosticLogs.Add(new DiagnosticLog
            {
                TenantId = tenantId,
                IncidentId = incidentId,
                StepName = "db_fix_executed",
                Result = $"Executed safe SQL on dedicated tenant DB. Rows affected: {rowsAffected}",
                IsSuccess = true,
                CreatedAt = DateTimeOffset.UtcNow
            });

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Tenant {TenantId} | Incident {IncidentId} | SQL executed successfully on dedicated tenant DB. Rows affected: {RowsAffected}", tenantId, incidentId, rowsAffected);
            return new DataFixResult(true, "SQL executed successfully.", rowsAffected);
        }
        catch (Exception ex)
        {
            try { await transaction.RollbackAsync(ct); }
            catch (Exception rbEx) { _logger.LogWarning(rbEx, "Tenant {TenantId} | Incident {IncidentId} | transaction rollback failed.", tenantId, incidentId); }

            await LogFailureAsync(tenantId, incidentId, ex, ct);

            _logger.LogError(ex, "Tenant {TenantId} | Incident {IncidentId} | SQL execution failed on dedicated tenant DB.", tenantId, incidentId);
            return new DataFixResult(false, $"Execution failed: {ex.Message}");
        }
    }

    private async Task LogFailureAsync(Guid tenantId, Guid incidentId, Exception ex, CancellationToken ct)
    {
        _db.DiagnosticLogs.Add(new DiagnosticLog
        {
            TenantId = tenantId,
            IncidentId = incidentId,
            StepName = "db_fix_failed",
            Result = $"Execution failed: {ex.Message}",
            IsSuccess = false,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);
    }
}
