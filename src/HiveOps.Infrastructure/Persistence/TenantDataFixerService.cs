using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using HiveOps.Application;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Infrastructure.Persistence;

public sealed class TenantDataFixerService : ITenantDataFixerService
{
    private readonly AppDbContext _db;
    private readonly TenantContext _tenantContext;
    private readonly ILogger<TenantDataFixerService> _logger;

    public TenantDataFixerService(AppDbContext db, TenantContext tenantContext, ILogger<TenantDataFixerService> logger)
    {
        _db = db;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public async Task<DataFixResult> ExecuteSafeSqlAsync(Guid tenantId, string sqlScript, Guid incidentId, CancellationToken ct = default)
    {
        if (!SafetyValidator.IsSafeSql(sqlScript))
        {
            var reason = SafetyValidator.GetRejectionReason(sqlScript);
            _logger.LogWarning("Tenant {TenantId} | Incident {IncidentId} | SQL rejected: {Reason}", tenantId, incidentId, reason);
            return new DataFixResult(false, $"SQL rejected by safety validator: {reason}");
        }

        // Ensure tenant context is set
        _tenantContext.SetTenant(tenantId);

        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
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

            _logger.LogInformation("Tenant {TenantId} | Incident {IncidentId} | SQL executed successfully. Rows affected: {RowsAffected}", tenantId, incidentId, rowsAffected);
            return new DataFixResult(true, "SQL executed successfully.", rowsAffected);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);

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

            _logger.LogError(ex, "Tenant {TenantId} | Incident {IncidentId} | SQL execution failed.", tenantId, incidentId);
            return new DataFixResult(false, $"Execution failed: {ex.Message}");
        }
    }
}
