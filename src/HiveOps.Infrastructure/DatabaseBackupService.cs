using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HiveOps.Infrastructure;

/// <summary>
/// Service to generate automatic backups before database modifications on external databases.
/// Uses tenant's EncryptedConnectionString to connect to external databases (HiveCrew, Sandpit, etc.).
/// </summary>
public sealed class DatabaseBackupService
{
    private readonly ILogger<DatabaseBackupService> _logger;
    private readonly DatabaseBackupOptions _options;

    public DatabaseBackupService(
        ILogger<DatabaseBackupService> logger,
        IOptions<DatabaseBackupOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Backs up a table before modification.
    /// </summary>
    public async Task<string> BackupTableAsync(
        string connectionString,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        var backupTableName = $"Backups_{tableName}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        _logger.LogInformation("Backing up table {TableName} to {BackupTableName}", tableName, backupTableName);

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"
                SELECT * INTO {backupTableName}
                FROM {tableName}";

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation("Backup completed successfully: {BackupTableName}", backupTableName);
            return backupTableName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to backup table {TableName}", tableName);
            throw;
        }
    }

    /// <summary>
    /// Restores a table from backup.
    /// </summary>
    public async Task RestoreTableAsync(
        string connectionString,
        string originalTableName,
        string backupTableName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Restoring table {OriginalTableName} from {BackupTableName}", originalTableName, backupTableName);

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // Drop original table
            using var dropCmd = connection.CreateCommand();
            dropCmd.CommandText = $"DROP TABLE {originalTableName}";
            await dropCmd.ExecuteNonQueryAsync(cancellationToken);

            // Rename backup to original
            using var renameCmd = connection.CreateCommand();
            renameCmd.CommandText = $@"
                EXEC sp_rename '{backupTableName}', '{originalTableName}'";
            await renameCmd.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation("Restore completed successfully: {OriginalTableName}", originalTableName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore table {OriginalTableName}", originalTableName);
            throw;
        }
    }

    /// <summary>
    /// Counts affected records for a SQL statement.
    /// </summary>
    public async Task<int> CountAffectedRecordsAsync(
        string connectionString,
        string sqlStatement,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // Extract WHERE clause to estimate affected records
            var countSql = EstimateCountSql(sqlStatement);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = countSql;

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is not null ? Convert.ToInt32(result) : 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to count affected records");
            return -1; // Indeterminate
        }
    }

    private string EstimateCountSql(string sqlStatement)
    {
        // Simple heuristic: convert UPDATE/DELETE to COUNT
        var upper = sqlStatement.ToUpperInvariant();

        if (upper.StartsWith("UPDATE"))
        {
            var whereIndex = upper.IndexOf(" WHERE ");
            if (whereIndex > 0)
            {
                var tableName = ExtractTableName(sqlStatement);
                var whereClause = sqlStatement.Substring(whereIndex);
                return $"SELECT COUNT(*) FROM {tableName} {whereClause}";
            }
        }
        else if (upper.StartsWith("DELETE"))
        {
            var whereIndex = upper.IndexOf(" WHERE ");
            if (whereIndex > 0)
            {
                var tableName = ExtractTableName(sqlStatement);
                var whereClause = sqlStatement.Substring(whereIndex);
                return $"SELECT COUNT(*) FROM {tableName} {whereClause}";
            }
        }

        // Default: return 1 for unknown
        return "SELECT 1";
    }

    private string ExtractTableName(string sqlStatement)
    {
        var upper = sqlStatement.ToUpperInvariant();
        var fromIndex = upper.IndexOf(" FROM ") + 6;
        var spaceAfterTable = upper.IndexOf(' ', fromIndex);

        if (spaceAfterTable > fromIndex)
            return sqlStatement.Substring(fromIndex, spaceAfterTable - fromIndex);

        return sqlStatement.Substring(fromIndex);
    }

    /// <summary>
    /// Cleans up old backup tables based on retention policy.
    /// </summary>
    public async Task CleanupOldBackupsAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        if (_options.RetentionDays <= 0)
            return;

        _logger.LogInformation("Cleaning up backups older than {RetentionDays} days", _options.RetentionDays);

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"
                DECLARE @CutoffDate DATE = DATEADD(DAY, -{_options.RetentionDays}, GETDATE())
                
                DECLARE @Sql NVARCHAR(MAX) = ''
                
                SELECT @Sql = @Sql + 'DROP TABLE [' + TABLE_NAME + '];'
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_NAME LIKE 'Backups_%'
                  AND TABLE_NAME LIKE '%' + CONVERT(VARCHAR(8), @CutoffDate, 112) + '%'
                
                EXEC sp_executesql @Sql";

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation("Backup cleanup completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup old backups");
        }
    }
}

public sealed class DatabaseBackupOptions
{
    public const string SectionName = "DatabaseBackup";

    public string BackupLocation { get; set; } = "./backups";
    public int RetentionDays { get; set; } = 30;
    public bool BackupToDatabase { get; set; } = true;
    public bool BackupToFile { get; set; } = false;
}
