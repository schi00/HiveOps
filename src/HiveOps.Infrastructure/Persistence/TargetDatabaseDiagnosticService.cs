using System.Data.Common;
using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;

namespace HiveOps.Infrastructure.Persistence;

/// <summary>
/// Read-only diagnostics over a tenant's own application database (e.g. HiveCrew).
/// Connection strings are resolved per-tenant via <see cref="IDynamicConnectionStringResolver"/>;
/// in SelfHosted mode a cold cache fails closed (<see cref="SecurityException"/>) and the
/// diagnostics degrade gracefully instead of touching another tenant's data.
/// </summary>
public sealed class TargetDatabaseDiagnosticService : ITargetDatabaseDiagnosticService
{
    private const int MaxCandidateTables = 30;
    private const int MaxKeywords = 12;
    private const int MaxRenderedRows = 50;
    private const int MaxCellLength = 120;
    private const int CommandTimeoutSeconds = 15;
    private static readonly TimeSpan SchemaCacheTtl = TimeSpan.FromMinutes(30);

    private static readonly Regex SelectStartPattern = new(
        @"^\s*SELECT\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ForbiddenSqlPattern = new(
        @"\b(INSERT|UPDATE|DELETE|DROP|ALTER|TRUNCATE|EXEC|EXECUTE|MERGE|GRANT)\b|\bINTO\s+\w+\s+FROM\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HasRowLimitPattern = new(
        @"\bLIMIT\b|\bTOP\b|\bOFFSET\b|\bFETCH\b|COUNT\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WordPattern = new(
        @"[A-Za-z0-9_]+",
        RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "because", "before", "being", "between", "could",
        "doing", "error", "failed", "failure", "getting", "having", "please", "should",
        "their", "there", "these", "thing", "those", "through", "users", "which",
        "while", "would", "cannot", "every", "still", "cuando", "desde", "donde",
        "estar", "estas", "estos", "falla", "fallo", "hacer", "hasta", "pero",
        "porque", "puede", "tiene", "tener", "todas", "todos", "siempre", "tambien"
    };

    private readonly IDynamicConnectionStringResolver _connectionStringResolver;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TargetDatabaseDiagnosticService> _logger;

    public TargetDatabaseDiagnosticService(
        IDynamicConnectionStringResolver connectionStringResolver,
        IMemoryCache cache,
        ILogger<TargetDatabaseDiagnosticService> logger)
    {
        _connectionStringResolver = connectionStringResolver;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TargetSchemaSummary> GetRelevantSchemaAsync(
        Guid tenantId, string incidentText, CancellationToken ct = default)
    {
        var keywords = ExtractKeywords(incidentText);
        var cacheKey = BuildSchemaCacheKey(tenantId, keywords);

        if (_cache.TryGetValue(cacheKey, out TargetSchemaSummary? cached) && cached is not null)
            return cached;

        string connectionString;
        try
        {
            await _connectionStringResolver.WarmCacheAsync(tenantId, ct);
            connectionString = _connectionStringResolver.Resolve(tenantId);
        }
        catch (SecurityException ex)
        {
            _logger.LogWarning(ex,
                "Tenant {TenantId} database connection is unavailable for schema diagnostics (fail-closed).", tenantId);
            return new TargetSchemaSummary(
                "-- tenant database connection unavailable (fail-closed)",
                Array.Empty<string>());
        }

        var tables = await LoadCandidateTablesAsync(connectionString, keywords, ct);
        if (tables.Count == 0)
        {
            var empty = new TargetSchemaSummary("-- no tables found in tenant database", Array.Empty<string>());
            _cache.Set(cacheKey, empty, SchemaCacheTtl);
            return empty;
        }

        var columns = await LoadColumnsAsync(connectionString, tables, ct);
        var ddl = FormatSchemaDdl(tables, columns);
        var candidateNames = tables
            .Select(t => $"{t.Schema}.{t.Name}")
            .ToList();

        var summary = new TargetSchemaSummary(ddl, candidateNames);
        _cache.Set(cacheKey, summary, new MemoryCacheEntryOptions().SetAbsoluteExpiration(SchemaCacheTtl));

        _logger.LogInformation(
            "Tenant {TenantId} schema summary built: {TableCount} candidate tables ({KeywordCount} keywords).",
            tenantId, tables.Count, keywords.Count);

        return summary;
    }

    /// <inheritdoc />
    public async Task<TargetQueryResult> ExecuteReadOnlyQueryAsync(
        Guid tenantId, string sqlQuery, CancellationToken ct = default)
    {
        var trimmed = (sqlQuery ?? string.Empty).Trim();

        if (!SelectStartPattern.IsMatch(trimmed))
            return new TargetQueryResult(false, string.Empty, 0, "Only SELECT statements are allowed.");

        if (ForbiddenSqlPattern.IsMatch(trimmed))
            return new TargetQueryResult(false, string.Empty, 0,
                "Query contains forbidden keywords; only read-only SELECT diagnostics are permitted.");

        string connectionString;
        try
        {
            await _connectionStringResolver.WarmCacheAsync(tenantId, ct);
            connectionString = _connectionStringResolver.Resolve(tenantId);
        }
        catch (SecurityException ex)
        {
            _logger.LogWarning(ex,
                "Tenant {TenantId} database connection is unavailable for query diagnostics (fail-closed).", tenantId);
            return new TargetQueryResult(false, string.Empty, 0,
                "Tenant database connection is unavailable.");
        }

        // Force a row cap unless the query already limits rows (LIMIT/OFFSET/FETCH) or aggregates.
        // The user query is wrapped in a subquery so LIMIT applies to the final result
        // even when the inner query has its own ORDER BY.
        var effectiveSql = HasRowLimitPattern.IsMatch(trimmed)
            ? trimmed
            : $"SELECT * FROM ({trimmed.TrimEnd(';')}) AS _diag LIMIT {MaxRenderedRows}";

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);

            await using (var setup = connection.CreateCommand())
            {
                // PostgreSQL maps READ UNCOMMITTED to READ COMMITTED anyway; keep it
                // explicit plus a statement/lock timeout for the diagnostic session.
                setup.CommandText =
                    "SET SESSION CHARACTERISTICS AS TRANSACTION ISOLATION LEVEL READ COMMITTED; " +
                    "SET lock_timeout = '5000'; SET statement_timeout = '15000';";
                setup.CommandTimeout = CommandTimeoutSeconds;
                await setup.ExecuteNonQueryAsync(ct);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = effectiveSql;
            command.CommandTimeout = CommandTimeoutSeconds;

            var rendered = new StringBuilder();
            var rowCount = 0;

            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (reader.FieldCount == 0)
                {
                    rendered.Append("(no result set)");
                }
                else
                {
                    var headers = new string[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                        headers[i] = EscapeCell(reader.GetName(i));

                    rendered.Append("| ").Append(string.Join(" | ", headers)).Append(" |\n");
                    rendered.Append('|').Append(string.Join("|", headers.Select(_ => " --- "))).Append("|\n");

                    while (await reader.ReadAsync(ct))
                    {
                        rowCount++;
                        if (rowCount > MaxRenderedRows)
                            continue; // keep counting real rows, stop rendering

                        var cells = new string[reader.FieldCount];
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            var value = await reader.IsDBNullAsync(i, ct)
                                ? null
                                : reader.GetValue(i);
                            cells[i] = EscapeCell(TruncateCell(value?.ToString()));
                        }

                        rendered.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
                    }

                    if (rowCount == 0)
                        rendered.Append("(0 rows)\n");
                }
            }

            _logger.LogInformation(
                "Tenant {TenantId} diagnostic query completed: {RowCount} rows in {ElapsedMs}ms.",
                tenantId, rowCount, stopwatch.ElapsedMilliseconds);

            return new TargetQueryResult(true, rendered.ToString().TrimEnd(), rowCount, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never log query results or data values — metadata only.
            _logger.LogWarning(ex,
                "Tenant {TenantId} diagnostic query failed after {ElapsedMs}ms.",
                tenantId, stopwatch.ElapsedMilliseconds);
            return new TargetQueryResult(false, string.Empty, 0, ex.Message);
        }
    }

    // ─── Schema discovery ───────────────────────────────────────────────────

    private static IReadOnlyList<string> ExtractKeywords(string? incidentText)
    {
        if (string.IsNullOrWhiteSpace(incidentText))
            return Array.Empty<string>();

        return WordPattern.Matches(incidentText)
            .Select(m => m.Value)
            .Where(w => w.Length > 4 && !StopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxKeywords)
            .ToList();
    }

    private static string BuildSchemaCacheKey(Guid tenantId, IReadOnlyList<string> keywords)
    {
        var joined = string.Join('|', keywords.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
        return $"tdiag:schema:{tenantId:D}:{hash[..16]}";
    }

    private async Task<List<(string Schema, string Name)>> LoadCandidateTablesAsync(
        string connectionString, IReadOnlyList<string> keywords, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;

        if (keywords.Count > 0)
        {
            var sql = new StringBuilder(
                "SELECT table_schema, table_name FROM information_schema.tables " +
                "WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema') AND (");

            for (var i = 0; i < keywords.Count; i++)
            {
                if (i > 0)
                    sql.Append(" OR ");
                sql.Append("table_name ILIKE @k").Append(i);
                command.Parameters.AddWithValue($"@k{i}", $"%{EscapeLikePattern(keywords[i])}%");
            }

            sql.Append(") ORDER BY table_schema, table_name");
            command.CommandText = sql.ToString();

            var matches = await ReadTableListAsync(command, ct);
            if (matches.Count > 0)
                return matches.Take(MaxCandidateTables).ToList();

            // No keyword matches: fall through to the most central tables.
            command.Parameters.Clear();
        }

        // Fallback: most-referenced tables by inbound foreign keys, then name.
        command.CommandText = """
            SELECT n.nspname, c.relname
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'r'
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND n.nspname NOT LIKE 'pg\_%'
            ORDER BY (SELECT COUNT(*) FROM pg_catalog.pg_constraint con
                      WHERE con.confrelid = c.oid AND con.contype = 'f') DESC,
                     c.relname ASC
            LIMIT @max
            """;
        command.Parameters.AddWithValue("@max", MaxCandidateTables);

        return await ReadTableListAsync(command, ct);
    }

    private static async Task<List<(string Schema, string Name)>> ReadTableListAsync(
        DbCommand command, CancellationToken ct)
    {
        var tables = new List<(string Schema, string Name)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            tables.Add((reader.GetString(0), reader.GetString(1)));
        return tables;
    }

    private static async Task<List<ColumnInfo>> LoadColumnsAsync(
        string connectionString, List<(string Schema, string Name)> tables, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;

        var sql = new StringBuilder("""
            SELECT c.table_schema, c.table_name, c.column_name, c.data_type,
                   c.character_maximum_length, c.numeric_precision, c.numeric_scale,
                   c.is_nullable,
                   CASE WHEN kcu.column_name IS NOT NULL THEN 1 ELSE 0 END AS is_primary_key
            FROM information_schema.columns c
            LEFT JOIN information_schema.table_constraints tc
                ON tc.table_schema = c.table_schema
               AND tc.table_name = c.table_name
               AND tc.constraint_type = 'PRIMARY KEY'
            LEFT JOIN information_schema.key_column_usage kcu
                ON kcu.constraint_name = tc.constraint_name
               AND kcu.table_schema = c.table_schema
               AND kcu.table_name = c.table_name
               AND kcu.column_name = c.column_name
            WHERE 
            """);

        for (var i = 0; i < tables.Count; i++)
        {
            if (i > 0)
                sql.Append(" OR ");
            sql.Append("(c.table_schema = @s").Append(i)
               .Append(" AND c.table_name = @t").Append(i).Append(')');
            command.Parameters.AddWithValue($"@s{i}", tables[i].Schema);
            command.Parameters.AddWithValue($"@t{i}", tables[i].Name);
        }

        sql.Append(" ORDER BY c.table_schema, c.table_name, c.ordinal_position");
        command.CommandText = sql.ToString();

        var columns = new List<ColumnInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            columns.Add(new ColumnInfo(
                Schema: reader.GetString(0),
                Table: reader.GetString(1),
                Name: reader.GetString(2),
                DataType: reader.GetString(3),
                MaxLength: reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4)),
                Precision: reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5)),
                Scale: reader.IsDBNull(6) ? null : Convert.ToInt32(reader.GetValue(6)),
                IsNullable: string.Equals(reader.GetString(7), "YES", StringComparison.OrdinalIgnoreCase),
                IsPrimaryKey: Convert.ToInt32(reader.GetValue(8)) == 1));
        }

        return columns;
    }

    private static string FormatSchemaDdl(
        List<(string Schema, string Name)> tables, List<ColumnInfo> columns)
    {
        var sb = new StringBuilder();
        foreach (var table in tables)
        {
            sb.Append("TABLE ").Append(table.Schema).Append('.').Append(table.Name).Append(" (");

            var first = true;
            foreach (var col in columns.Where(c =>
                         c.Schema == table.Schema && c.Table == table.Name))
            {
                if (!first)
                    sb.Append(", ");
                first = false;

                sb.Append(col.Name).Append(' ').Append(FormatDataType(col));
                if (col.IsPrimaryKey)
                    sb.Append(" PK");
                if (!col.IsNullable)
                    sb.Append(" NOT NULL");
            }

            sb.Append(")\n");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatDataType(ColumnInfo col)
    {
        var type = col.DataType;
        if (col.MaxLength is { } len &&
            type is "character" or "character varying" or "varchar" or "char" or "bit" or "bit varying")
        {
            return $"{type}({len})";
        }

        if (col.Precision is { } precision && type is "decimal" or "numeric")
        {
            return $"{type}({precision},{col.Scale ?? 0})";
        }

        return type;
    }

    // ─── Render helpers ─────────────────────────────────────────────────────

    private static string TruncateCell(string? value)
    {
        if (value is null)
            return "NULL";
        return value.Length > MaxCellLength ? value[..MaxCellLength] + "…" : value;
    }

    private static string EscapeCell(string value) =>
        value.Replace("|", "\\|").Replace('\n', ' ').Replace('\r', ' ');

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed record ColumnInfo(
        string Schema,
        string Table,
        string Name,
        string DataType,
        int? MaxLength,
        int? Precision,
        int? Scale,
        bool IsNullable,
        bool IsPrimaryKey);
}
