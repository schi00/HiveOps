namespace HiveOps.Domain.Interfaces;

public interface ITenantDataFixerService
{
    /// <summary>
    /// Executes a validated SQL script against the tenant database.
    /// Returns a diagnostic log entry describing the result.
    /// </summary>
    Task<DataFixResult> ExecuteSafeSqlAsync(Guid tenantId, string sqlScript, Guid incidentId, CancellationToken ct = default);
}

public sealed record DataFixResult(bool Success, string Message, int RowsAffected = 0);
