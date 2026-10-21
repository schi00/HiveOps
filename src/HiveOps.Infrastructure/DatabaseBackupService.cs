using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HiveOps.Application.Support;

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
        ValidateTableIdentifier(tableName);

        var backupTableName = BuildBackupTableName(tableName);

        _logger.LogInformation("Backing up table {TableName} to {BackupTableName}", tableName, backupTableName);

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // Validate table exists before attempting backup
            using (var existsCmd = connection.CreateCommand())
            {
                existsCmd.CommandText = "SELECT CASE WHEN OBJECT_ID(@tn) IS NOT NULL THEN 1 ELSE 0 END";
                existsCmd.Parameters.AddWithValue("@tn", tableName);
                var existsObj = await existsCmd.ExecuteScalarAsync(cancellationToken);
                var exists = existsObj is not null && Convert.ToInt32(existsObj) == 1;
                if (!exists)
                {
                    _logger.LogError("Table {TableName} does not exist, skipping backup.", tableName);
                    throw new InvalidOperationException($"Table '{tableName}' does not exist.");
                }
            }

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

    // Simple identifier validation to avoid SQL injection through table names
    // (table identifiers cannot be parameterized in T-SQL).
    private static readonly Regex TableIdentifierPattern = new(
        @"^(?:\[?\w+\]?\.)?\[?\w+\]?$",
        RegexOptions.Compiled);

    private static string BuildBackupTableName(string tableName)
    {
        var flatName = tableName.Replace("[", "").Replace("]", "").Replace('.', '_');
        return $"Backups_{flatName}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
    }

    private static void ValidateTableIdentifier(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName) || !TableIdentifierPattern.IsMatch(tableName))
            throw new ArgumentException($"Invalid table identifier: '{tableName}'.", nameof(tableName));
    }

    /// <summary>
    /// Backs up ONLY the rows targeted by an UPDATE/DELETE fix, instead of the
    /// whole table. The <paramref name="whereClause"/> must include the WHERE
    /// keyword (see <see cref="SqlStatementParser.ExtractWhereClause"/>); when
    /// null/empty it behaves like <see cref="BackupTableAsync"/>.
    /// </summary>
    public async Task<string> BackupAffectedRowsAsync(
        string connectionString,
        string tableName,
        string? whereClause,
        CancellationToken cancellationToken = default)
    {
        ValidateTableIdentifier(tableName);

        var backupTableName = BuildBackupTableName(tableName);

        _logger.LogInformation(
            "Backing up affected rows of {TableName} to {BackupTableName} (where: {WhereClause})",
            tableName, backupTableName, whereClause ?? "<none>");

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // Validate table exists before attempting backup
            using (var existsCmd = connection.CreateCommand())
            {
                existsCmd.CommandText = "SELECT CASE WHEN OBJECT_ID(@tn) IS NOT NULL THEN 1 ELSE 0 END";
                existsCmd.Parameters.AddWithValue("@tn", tableName);
                var existsObj = await existsCmd.ExecuteScalarAsync(cancellationToken);
                var exists = existsObj is not null && Convert.ToInt32(existsObj) == 1;
                if (!exists)
                {
                    _logger.LogError("Table {TableName} does not exist, skipping backup.", tableName);
                    throw new InvalidOperationException($"Table '{tableName}' does not exist.");
                }
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = string.IsNullOrWhiteSpace(whereClause)
                ? $"SELECT * INTO {backupTableName} FROM {tableName}"
                : $"SELECT * INTO {backupTableName} FROM {tableName} {whereClause}";

            var backedUpRows = await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation(
                "Selective backup completed: {BackupTableName} ({Rows} rows)", backupTableName, backedUpRows);
            return backupTableName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to backup affected rows of table {TableName}", tableName);
            throw;
        }
    }

    /// <summary>
    /// Restores the rows saved by <see cref="BackupAffectedRowsAsync"/>.
    /// Pragmatic strategy for selective backups: rows currently matching the
    /// WHERE clause are deleted and the backed-up rows are re-inserted, so only
    /// the rowset touched by the fix is restored (the rest of the table is
    /// never dropped). For full-table backups use <see cref="RestoreTableAsync"/>.
    /// </summary>
    public async Task RestoreAffectedRowsAsync(
        string connectionString,
        string originalTableName,
        string backupTableName,
        string? whereClause,
        CancellationToken cancellationToken = default)
    {
        ValidateTableIdentifier(originalTableName);
        ValidateTableIdentifier(backupTableName);

        _logger.LogInformation(
            "Restoring affected rows of {OriginalTableName} from {BackupTableName} (where: {WhereClause})",
            originalTableName, backupTableName, whereClause ?? "<none>");

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                // Re-delete the rowset the fix touched (idempotent) ...
                if (!string.IsNullOrWhiteSpace(whereClause))
                {
                    await using var deleteCmd = connection.CreateCommand();
                    deleteCmd.Transaction = transaction;
                    deleteCmd.CommandText = $"DELETE FROM {originalTableName} {whereClause}";
                    await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                else
                {
                    // Without a WHERE clause the backup covered the whole table.
                    await using var truncateCmd = connection.CreateCommand();
                    truncateCmd.Transaction = transaction;
                    truncateCmd.CommandText = $"DELETE FROM {originalTableName}";
                    await truncateCmd.ExecuteNonQueryAsync(cancellationToken);
                }

                // ... then re-insert the pre-fix rows captured by the selective backup.
                await using var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = $"INSERT INTO {originalTableName} SELECT * FROM {backupTableName}";
                var restored = await insertCmd.ExecuteNonQueryAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation(
                    "Selective restore completed: {OriginalTableName} ({Rows} rows re-inserted)",
                    originalTableName, restored);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore affected rows of {OriginalTableName}", originalTableName);
            throw;
        }
    }

    /// <summary>
    /// Restores a table from a FULL-table backup created by
    /// <see cref="BackupTableAsync"/>: drops the original table and renames the
    /// backup into place. Do NOT use this for backups created by
    /// <see cref="BackupAffectedRowsAsync"/> (those contain only a subset of
    /// rows) — use <see cref="RestoreAffectedRowsAsync"/> instead.
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

        if (upper.StartsWith("UPDATE") || upper.StartsWith("DELETE"))
        {
            var tableName = SqlStatementParser.ExtractTableName(sqlStatement);
            var whereClause = SqlStatementParser.ExtractWhereClause(sqlStatement);
            if (!string.IsNullOrEmpty(tableName) && !string.IsNullOrEmpty(whereClause))
            {
                return $"SELECT COUNT(*) FROM {tableName} {whereClause}";
            }
        }

        // Default: return 1 for unknown
        return "SELECT 1";
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
