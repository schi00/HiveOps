using System.Text;
using Npgsql;
using Microsoft.Extensions.Logging;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;

namespace HiveOps.Infrastructure.Diagnostics;

/// <summary>
/// Collects recent application log entries from the tenant's own target database.
/// Discovers a log-like table (<c>Logs</c>, <c>AppLogs</c>, <c>ErrorLogs</c>,
/// <c>SystemLogs</c>, <c>Serilog</c>, <c>EventLog</c>, <c>AuditLogs</c>, or any
/// <c>*Log*</c> table) via <c>INFORMATION_SCHEMA</c>, maps its columns heuristically,
/// and reads the newest rows under READ UNCOMMITTED with a short timeout.
///
/// Safety guarantees:
/// 1. Strictly read-only — no statement other than SELECT is ever executed.
/// 2. Identifiers are bracket-quoted and all values go through parameters.
/// 3. Any failure (no connection, no log table, missing columns, SQL error)
///    returns an empty list; the collector never throws and never writes.
/// </summary>
public sealed class TargetDatabaseLogCollector : ITargetLogCollector
{
    private const int MaxRows = 20;
    private const int CommandTimeoutSeconds = 5;
    private const int MaxHintLength = 60;

    // Column-name candidates in priority order; the first match wins.
    private static readonly string[] TimestampColumnCandidates = ["TimeStamp", "Timestamp", "CreatedAt", "Date", "LoggedAt"];
    private static readonly string[] LevelColumnCandidates = ["Level", "Severity", "LogLevel"];
    private static readonly string[] MessageColumnCandidates = ["Message", "RenderedMessage", "Text", "Msg"];
    private static readonly string[] ExceptionColumnCandidates = ["Exception", "ExceptionText", "StackTrace"];

    private readonly IDynamicConnectionStringResolver _connectionStringResolver;
    private readonly ILogger<TargetDatabaseLogCollector> _logger;

    public TargetDatabaseLogCollector(
        IDynamicConnectionStringResolver connectionStringResolver,
        ILogger<TargetDatabaseLogCollector> logger)
    {
        _connectionStringResolver = connectionStringResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppLogEvidence>> CollectRecentErrorsAsync(
        Guid tenantId, string searchHint, TimeSpan lookback, CancellationToken ct = default)
    {
        string connectionString;
        try
        {
            await _connectionStringResolver.WarmCacheAsync(tenantId, ct);
            connectionString = _connectionStringResolver.Resolve(tenantId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Tenant {TenantId} database connection is unavailable; log collection skipped.", tenantId);
            return [];
        }

        try
        {
            return await CollectAsync(connectionString, tenantId, searchHint, lookback, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Log collection failed for tenant {TenantId}; returning empty evidence.", tenantId);
            return [];
        }
    }

    private async Task<IReadOnlyList<AppLogEvidence>> CollectAsync(
        string connectionString, Guid tenantId, string searchHint, TimeSpan lookback, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                "SET SESSION CHARACTERISTICS AS TRANSACTION ISOLATION LEVEL READ COMMITTED; " +
                "SET lock_timeout = '5000'; SET statement_timeout = '8000';";
            setup.CommandTimeout = CommandTimeoutSeconds;
            await setup.ExecuteNonQueryAsync(ct);
        }

        var table = await FindLogTableAsync(connection, ct);
        if (table is null)
        {
            _logger.LogDebug("Tenant {TenantId}: no log-like table found; skipping log collection.", tenantId);
            return [];
        }

        var columns = await LoadColumnNamesAsync(connection, table.Value.Schema, table.Value.Name, ct);
        var timestampColumn = PickColumn(columns, TimestampColumnCandidates);
        var messageColumn = PickColumn(columns, MessageColumnCandidates);
        if (timestampColumn is null || messageColumn is null)
        {
            _logger.LogWarning(
                "Tenant {TenantId}: log table {Table} lacks a usable timestamp/message column mapping; skipping.",
                tenantId, $"{table.Value.Schema}.{table.Value.Name}");
            return [];
        }

        var levelColumn = PickColumn(columns, LevelColumnCandidates);
        var exceptionColumn = PickColumn(columns, ExceptionColumnCandidates);

        return await ReadLogRowsAsync(
            connection, tenantId, table.Value.Schema, table.Value.Name,
            timestampColumn, levelColumn, messageColumn, exceptionColumn,
            searchHint, lookback, ct);
    }

    private static async Task<(string Schema, string Name)?> FindLogTableAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT table_schema, table_name
            FROM information_schema.tables
            WHERE table_type = 'BASE TABLE'
              AND table_schema NOT IN ('pg_catalog', 'information_schema')
              AND (table_name ILIKE '%log%'
                   OR lower(table_name) IN ('logs', 'applogs', 'errorlogs', 'systemlogs',
                                            'serilog', 'eventlog', 'auditlogs'))
            ORDER BY table_schema, table_name
            LIMIT 1
            """;

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async Task<List<string>> LoadColumnNamesAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
            ORDER BY ordinal_position
            """;
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task<IReadOnlyList<AppLogEvidence>> ReadLogRowsAsync(
        NpgsqlConnection connection,
        Guid tenantId,
        string schema,
        string table,
        string timestampColumn,
        string? levelColumn,
        string messageColumn,
        string? exceptionColumn,
        string searchHint,
        TimeSpan lookback,
        CancellationToken ct)
    {
        var selectList = new StringBuilder(QuoteIdentifier(timestampColumn));
        if (levelColumn is not null)
            selectList.Append(", ").Append(QuoteIdentifier(levelColumn));
        selectList.Append(", ").Append(QuoteIdentifier(messageColumn));
        if (exceptionColumn is not null)
            selectList.Append(", ").Append(QuoteIdentifier(exceptionColumn));

        var sql = new StringBuilder()
            .Append("SELECT ").Append(selectList)
            .Append(" FROM ").Append(QuoteIdentifier(schema)).Append('.').Append(QuoteIdentifier(table))
            .Append(" WHERE ").Append(QuoteIdentifier(timestampColumn)).Append(" >= @since");

        var hint = BuildLikeHint(searchHint);
        if (hint is not null)
            sql.Append(" AND ").Append(QuoteIdentifier(messageColumn)).Append(" ILIKE @hint");

        sql.Append(" ORDER BY ").Append(QuoteIdentifier(timestampColumn)).Append(" DESC")
           .Append(" LIMIT ").Append(MaxRows);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = sql.ToString();
        // Use a plain DateTime parameter: it binds natively to datetime/datetime2
        // columns and converts implicitly for datetimeoffset columns.
        command.Parameters.AddWithValue("@since", DateTime.UtcNow - lookback);
        if (hint is not null)
            command.Parameters.AddWithValue("@hint", hint);

        var entries = new List<AppLogEvidence>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var columnIndex = 0;
            var timestamp = await reader.IsDBNullAsync(columnIndex, ct)
                ? DateTimeOffset.MinValue
                : ToDateTimeOffset(reader.GetValue(columnIndex));
            columnIndex++;

            var level = string.Empty;
            if (levelColumn is not null)
            {
                level = await reader.IsDBNullAsync(columnIndex, ct)
                    ? string.Empty
                    : reader.GetValue(columnIndex)?.ToString() ?? string.Empty;
                columnIndex++;
            }

            var message = await reader.IsDBNullAsync(columnIndex, ct)
                ? string.Empty
                : reader.GetValue(columnIndex)?.ToString() ?? string.Empty;
            columnIndex++;

            string? exceptionText = null;
            if (exceptionColumn is not null)
            {
                exceptionText = await reader.IsDBNullAsync(columnIndex, ct)
                    ? null
                    : reader.GetValue(columnIndex)?.ToString();
            }

            entries.Add(new AppLogEvidence(
                timestamp,
                level,
                message,
                exceptionText,
                $"{schema}.{table}"));
        }

        return entries;
    }

    /// <summary>Returns the actual column name for the first candidate present, or null.</summary>
    private static string? PickColumn(IReadOnlyList<string> columns, IReadOnlyList<string> candidates) =>
        candidates.FirstOrDefault(candidate =>
            columns.Any(c => string.Equals(c, candidate, StringComparison.OrdinalIgnoreCase)));

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    /// <summary>
    /// Builds a <c>%hint%</c> LIKE pattern from the first <see cref="MaxHintLength"/>
    /// characters of the search hint, escaping LIKE wildcards (\ % _) with a
    /// backslash — PostgreSQL's default LIKE escape character.
    /// Returns null when the hint has no usable content.
    /// </summary>
    private static string? BuildLikeHint(string? searchHint)
    {
        if (string.IsNullOrWhiteSpace(searchHint))
            return null;

        var trimmed = searchHint.Trim();
        if (trimmed.Length > MaxHintLength)
            trimmed = trimmed[..MaxHintLength];

        var escaped = trimmed
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

        return $"%{escaped}%";
    }

    private static DateTimeOffset ToDateTimeOffset(object value) => value switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(
            dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime()),
        _ => DateTimeOffset.TryParse(value.ToString(), out var parsed) ? parsed : DateTimeOffset.MinValue
    };
}
