using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Data.Common;
using HiveOps.Application.Support;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HiveOps.Infrastructure.Persistence;

/// <summary>
/// Captures row-level before/after snapshots for governed DB remediations and
/// generates PK-keyed inverse SQL for surgical rollback. Replaces full-table
/// backups as the primary safety mechanism for data fixes: the forward script
/// is parsed (<see cref="SqlStatementParser"/>), the rows it will touch are
/// captured as JSON (<c>BeforeRowsJson</c>/<c>AfterRowsJson</c>) and an exact
/// inverse script is generated keyed by the table's real primary key.
/// </summary>
/// <remarks>
/// The pure, DB-free logic (inverse-SQL generation, JSON row serialization,
/// literal rendering, PK fallback selection) lives in
/// <see cref="RowSnapshotSql"/> so unit tests can exercise it without a
/// database connection.
/// </remarks>
public sealed class RowLevelSnapshotService : IRowLevelSnapshotService
{
    private const int CommandTimeoutSeconds = 30;
    private const int MaxCapturedRows = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RowLevelSnapshotService> _logger;

    public RowLevelSnapshotService(
        IServiceScopeFactory scopeFactory,
        ILogger<RowLevelSnapshotService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IncidentRemediationSnapshot> CaptureBeforeSnapshotAsync(
        Guid tenantId, Guid incidentId, string connectionString,
        string forwardSql, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(forwardSql))
            throw new InvalidOperationException("Forward SQL script is empty; nothing to snapshot.");

        // 1. Split and classify: every mutation statement must target ONE table.
        var mutations = ClassifyMutations(forwardSql);
        var targetTable = mutations[0].Table;
        if (mutations.Any(m => !string.Equals(m.Table, targetTable, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Forward script mutates multiple tables ({string.Join(", ", mutations.Select(m => m.Table).Distinct())}); " +
                "row-level snapshots require a single target table.");
        }

        var kinds = mutations.Select(m => m.Kind).Distinct().ToList();
        if (kinds.Count > 1)
        {
            throw new InvalidOperationException(
                "Forward script mixes mutation kinds (UPDATE/DELETE/INSERT); " +
                "a reliable inverse script cannot be generated for heterogeneous scripts.");
        }
        var kind = kinds[0];

        RowSnapshotSql.ValidateTableIdentifier(targetTable);
        var (schema, table) = RowSnapshotSql.SplitTableIdentifier(targetTable);

        _logger.LogInformation(
            "Capturing before-snapshot for incident {IncidentId}: {Kind} on {TargetTable} ({Statements} statement(s)).",
            incidentId, kind, targetTable, mutations.Count);

        // 2. Schema discovery + row capture against the tenant database.
        List<Dictionary<string, object?>> beforeRows;
        List<Dictionary<string, object?>> insertedPkRows = new();
        string inverseSql;
        IReadOnlyList<string> pkColumns;

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(ct);

            // 3. Resolve the real primary key (fallback: all comparable columns).
            pkColumns = await LoadPrimaryKeyColumnsAsync(connection, schema, table, ct);
            if (pkColumns.Count == 0)
            {
                var allColumns = await LoadTableColumnsAsync(connection, schema, table, ct);
                pkColumns = RowSnapshotSql.SelectFallbackKeyColumns(allColumns).ToList();
                _logger.LogWarning(
                    "Table {TargetTable} has no declared PRIMARY KEY; using fallback composite key ({ColumnCount} columns).",
                    targetTable, pkColumns.Count);
            }

            if (pkColumns.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Table '{targetTable}' has no primary key and no usable columns for a fallback row identity; " +
                    "cannot capture a row-level snapshot.");
            }

            switch (kind)
            {
                case RowSnapshotSql.StatementKind.Update:
                case RowSnapshotSql.StatementKind.Delete:
                {
                    // OR together the WHERE clauses of every mutation statement so
                    // the captured rowset covers everything the fix can touch.
                    var combinedWhere = RowSnapshotSql.BuildCombinedWhereClause(
                        mutations.Select(m => m.Sql).ToList());
                    var captureSql = $"SELECT * FROM {targetTable} {combinedWhere} LIMIT {MaxCapturedRows}".TrimEnd();

                    beforeRows = await ExecuteRowQueryAsync(connection, captureSql, ct);

                    var hasIdentity = kind == RowSnapshotSql.StatementKind.Delete
                        && await TableHasIdentityColumnAsync(connection, schema, table, ct);

                    inverseSql = kind == RowSnapshotSql.StatementKind.Update
                        ? RowSnapshotSql.BuildUpdateInverse(targetTable, pkColumns, beforeRows)
                        : RowSnapshotSql.BuildDeleteInverse(targetTable, pkColumns, beforeRows, hasIdentity);
                    break;
                }

                case RowSnapshotSql.StatementKind.Insert:
                {
                    // Rows do not exist yet; parse the literal PK values out of
                    // the INSERT ... VALUES clauses to build inverse DELETEs.
                    beforeRows = new List<Dictionary<string, object?>>();

                    foreach (var mutation in mutations)
                    {
                        var parsed = RowSnapshotSql.ParseInsertStatement(mutation.Sql);
                        var columnIndex = parsed.Columns
                            .Select((name, idx) => (name, idx))
                            .ToDictionary(x => x.name, x => x.idx, StringComparer.OrdinalIgnoreCase);

                        foreach (var pkColumn in pkColumns)
                        {
                            if (!columnIndex.ContainsKey(pkColumn))
                            {
                                throw new InvalidOperationException(
                                    $"INSERT statement does not provide a literal value for primary key column '{pkColumn}' " +
                                    $"on table '{targetTable}'; inserted rows cannot be identified for rollback.");
                            }
                        }

                        foreach (var row in parsed.Rows)
                        {
                            var pkRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                            foreach (var pkColumn in pkColumns)
                                pkRow[pkColumn] = row[columnIndex[pkColumn]];
                            insertedPkRows.Add(pkRow);
                        }
                    }

                    inverseSql = RowSnapshotSql.BuildInsertInverse(targetTable, pkColumns, insertedPkRows);
                    break;
                }

                default:
                    throw new InvalidOperationException($"Unsupported statement kind '{kind}'.");
            }
        }

        // 4. Persist the snapshot in the platform database under the tenant scope.
        var snapshot = new IncidentRemediationSnapshot
        {
            TenantId = tenantId,
            IncidentId = incidentId,
            TargetTable = targetTable,
            PrimaryKeyColumnsJson = JsonSerializer.Serialize(pkColumns),
            BeforeRowsJson = RowSnapshotSql.SerializeRows(beforeRows),
            AfterRowsJson = "[]",
            InverseSqlScript = inverseSql,
            ForwardSqlScript = forwardSql,
            AffectedRowCount = kind == RowSnapshotSql.StatementKind.Insert
                ? insertedPkRows.Count
                : beforeRows.Count
        };

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            ResolveTenantContext(scope, tenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.IncidentRemediationSnapshots.Add(snapshot);
            await db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Snapshot {SnapshotId} captured for incident {IncidentId}: {Rows} row(s) on {TargetTable}.",
            snapshot.Id, incidentId, snapshot.AffectedRowCount, targetTable);

        return snapshot;
    }

    /// <inheritdoc />
    public async Task PopulateAfterSnapshotAsync(
        IncidentRemediationSnapshot snapshot, string connectionString, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var pkColumns = JsonSerializer.Deserialize<List<string>>(snapshot.PrimaryKeyColumnsJson) ?? new List<string>();
        var kind = ResolveForwardKind(snapshot.ForwardSqlScript);

        IReadOnlyList<Dictionary<string, object?>> identityRows;
        if (kind == RowSnapshotSql.StatementKind.Insert)
        {
            // Rows were identified by their literal PK values in the INSERT.
            var mutations = ClassifyMutations(snapshot.ForwardSqlScript);
            var pkRows = new List<Dictionary<string, object?>>();
            foreach (var mutation in mutations)
            {
                var parsed = RowSnapshotSql.ParseInsertStatement(mutation.Sql);
                var columnIndex = parsed.Columns
                    .Select((name, idx) => (name, idx))
                    .ToDictionary(x => x.name, x => x.idx, StringComparer.OrdinalIgnoreCase);
                foreach (var row in parsed.Rows)
                {
                    var pkRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pkColumn in pkColumns)
                        pkRow[pkColumn] = row[columnIndex[pkColumn]];
                    pkRows.Add(pkRow);
                }
            }
            identityRows = pkRows;
        }
        else
        {
            identityRows = RowSnapshotSql.DeserializeRows(snapshot.BeforeRowsJson);
        }

        var lookupWhere = RowSnapshotSql.BuildRowLookupWhere(pkColumns, identityRows);
        var afterRows = new List<Dictionary<string, object?>>();
        if (lookupWhere is not null)
        {
            var selectSql = $"SELECT * FROM {snapshot.TargetTable} {lookupWhere} LIMIT {MaxCapturedRows}";
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            afterRows = await ExecuteRowQueryAsync(connection, selectSql, ct);
        }

        snapshot.AfterRowsJson = RowSnapshotSql.SerializeRows(afterRows);

        await using var scope = _scopeFactory.CreateAsyncScope();
        ResolveTenantContext(scope, snapshot.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.IncidentRemediationSnapshots.Update(snapshot);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Snapshot {SnapshotId} after-capture completed: {Rows} row(s).",
            snapshot.Id, afterRows.Count);
    }

    /// <inheritdoc />
    public async Task<int> RevertSnapshotAsync(
        IncidentRemediationSnapshot snapshot, string connectionString,
        string revertedBy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.IsReverted)
        {
            _logger.LogInformation(
                "Snapshot {SnapshotId} is already reverted; skipping.", snapshot.Id);
            return 0;
        }

        if (string.IsNullOrWhiteSpace(snapshot.InverseSqlScript))
        {
            _logger.LogWarning(
                "Snapshot {SnapshotId} has no inverse script; nothing to revert.", snapshot.Id);
            return 0;
        }

        var affected = 0;
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(ct);
            await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(ct);

            try
            {
                foreach (var statement in SqlStatementParser.SplitStatements(snapshot.InverseSqlScript))
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = statement;
                    command.CommandTimeout = CommandTimeoutSeconds;
                    var n = await command.ExecuteNonQueryAsync(ct);
                    if (n > 0)
                        affected += n;
                }

                await transaction.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                _logger.LogError(ex,
                    "Failed to revert snapshot {SnapshotId} on table {TargetTable}.",
                    snapshot.Id, snapshot.TargetTable);
                throw;
            }
        }

        snapshot.IsReverted = true;
        snapshot.RevertedAt = DateTimeOffset.UtcNow;
        snapshot.RevertedBy = revertedBy;

        await using var scope = _scopeFactory.CreateAsyncScope();
        ResolveTenantContext(scope, snapshot.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.IncidentRemediationSnapshots.Update(snapshot);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Snapshot {SnapshotId} reverted by {RevertedBy}: {Rows} row(s) affected.",
            snapshot.Id, revertedBy, affected);

        return affected;
    }

    // ─── Internal helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Splits the forward script and keeps only the mutation statements
    /// (UPDATE/DELETE/INSERT), resolving each one's target table.
    /// Throws when the script contains no mutation at all.
    /// </summary>
    private static List<(string Sql, RowSnapshotSql.StatementKind Kind, string Table)> ClassifyMutations(string sql)
    {
        var mutations = new List<(string Sql, RowSnapshotSql.StatementKind Kind, string Table)>();

        foreach (var statement in SqlStatementParser.SplitStatements(sql))
        {
            var kind = RowSnapshotSql.ClassifyStatement(statement);
            if (kind is null)
                continue; // non-mutation statement (SET/PRINT/SELECT/...): skip.

            var table = SqlStatementParser.ExtractTableName(statement);
            if (string.IsNullOrWhiteSpace(table))
            {
                throw new InvalidOperationException(
                    $"Cannot determine the target table of mutation statement: '{RowSnapshotSql.Truncate(statement, 120)}'.");
            }

            mutations.Add((statement, kind.Value, table));
        }

        if (mutations.Count == 0)
        {
            throw new InvalidOperationException(
                "Forward script contains no UPDATE/DELETE/INSERT mutation statements; nothing to snapshot.");
        }

        return mutations;
    }

    /// <summary>
    /// Determines the single mutation kind of a forward script. Returns the kind
    /// shared by all mutation statements; throws for empty/mixed scripts.
    /// </summary>
    private static RowSnapshotSql.StatementKind ResolveForwardKind(string forwardSql)
    {
        var mutations = ClassifyMutations(forwardSql);
        var kinds = mutations.Select(m => m.Kind).Distinct().ToList();
        if (kinds.Count > 1)
            throw new InvalidOperationException("Forward script mixes mutation kinds.");
        return kinds[0];
    }

    /// <summary>Runs a SELECT and materializes every row as a column/value dictionary.</summary>
    private static async Task<List<Dictionary<string, object?>>> ExecuteRowQueryAsync(
        NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Returns the declared PRIMARY KEY columns of a table, in ordinal position order.</summary>
    private static async Task<IReadOnlyList<string>> LoadPrimaryKeyColumnsAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON kcu.constraint_name = tc.constraint_name
             AND kcu.table_schema = tc.table_schema
             AND kcu.table_name = tc.table_name
            WHERE tc.constraint_type = 'PRIMARY KEY'
              AND tc.table_schema = @schema
              AND tc.table_name = @table
            ORDER BY kcu.ordinal_position
            """;
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(0));
        return columns;
    }

    /// <summary>Returns all columns of a table with their SQL data type names.</summary>
    private static async Task<IReadOnlyList<(string Name, string DataType)>> LoadTableColumnsAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
            ORDER BY ordinal_position
            """;
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);

        var columns = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add((reader.GetString(0), reader.GetString(1)));
        return columns;
    }

    /// <summary>
    /// Returns true when the table has at least one GENERATED ALWAYS AS IDENTITY
    /// column — the only PostgreSQL identity flavor that rejects explicit values
    /// and therefore needs OVERRIDING SYSTEM VALUE on INSERT.
    /// </summary>
    private static async Task<bool> TableHasIdentityColumnAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT COUNT(*)
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
              AND a.attnum > 0 AND NOT a.attisdropped AND a.attidentity = 'a'
            """;
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);

        var result = await command.ExecuteScalarAsync(ct);
        return result is not null && Convert.ToInt32(result, CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// Resolves the scope tenant so EF query filters stay tenant-correct
    /// (same pattern as IncidentAutoWorkflowService).
    /// </summary>
    private static void ResolveTenantContext(AsyncServiceScope scope, Guid tenantId)
    {
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        if (tenantContext is TenantContext tc && !tc.IsResolved)
            tc.SetTenant(tenantId);
    }
}

/// <summary>
/// Pure, DB-free SQL helpers used by <see cref="RowLevelSnapshotService"/>:
/// statement classification, combined WHERE building, inverse-SQL generation,
/// JSON row serialization and PostgreSQL literal rendering. Public so unit tests
/// can exercise them without a database connection.
/// </summary>
public static class RowSnapshotSql
{
    /// <summary>Mutation kind supported by the snapshot service.</summary>
    public enum StatementKind
    {
        Update,
        Delete,
        Insert
    }

    /// <summary>SQL data types excluded from the fallback composite row identity.</summary>
    private static readonly HashSet<string> FallbackExcludedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "xml", "bytea"
    };

    // Simple identifier validation to avoid SQL injection through table names
    // (table identifiers cannot be parameterized). Accepts bare, [bracketed] and
    // "double-quoted" identifiers; optional single schema qualifier.
    private static readonly Regex TableIdentifierPattern = new(
        @"^(?:(?:\[\w+\]|""[^""]+""|\w+)\.)?(?:\[\w+\]|""[^""]+""|\w+)$",
        RegexOptions.Compiled);

    private static readonly Regex InsertHeadPattern = new(
        @"\bINSERT\s+INTO\s+(?:(?:\[\w+\]|""[^""]+""|\w+)\.)?(?:\[\w+\]|""[^""]+""|\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WordPattern = new(@"\G\s*(?<word>\w+)", RegexOptions.Compiled);

    /// <summary>Throws when <paramref name="tableName"/> is not a safe SQL identifier.</summary>
    public static void ValidateTableIdentifier(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName) || !TableIdentifierPattern.IsMatch(tableName))
            throw new ArgumentException($"Invalid table identifier: '{tableName}'.", nameof(tableName));
    }

    /// <summary>Double-quotes a single column identifier, escaping inner quotes.</summary>
    public static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Trim('"', '[', ']').Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// Splits a possibly schema-qualified identifier ("public.orders", "sales.T", "orders")
    /// into its schema and table parts. Defaults the schema to "public".
    /// Accepts bare, [bracketed] and "double-quoted" parts.
    /// </summary>
    public static (string Schema, string Table) SplitTableIdentifier(string identifier)
    {
        var flat = identifier.Replace("[", "").Replace("]", "").Replace("\"", "");
        var parts = flat.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            1 => ("public", parts[0]),
            2 => (parts[0], parts[1]),
            _ => throw new ArgumentException($"Invalid table identifier: '{identifier}'.", nameof(identifier))
        };
    }

    /// <summary>
    /// Classifies a single statement by its leading keyword, skipping leading
    /// comments. Returns null for non-mutation statements.
    /// </summary>
    public static StatementKind? ClassifyStatement(string statement)
    {
        return GetLeadingKeyword(statement) switch
        {
            "UPDATE" => StatementKind.Update,
            "DELETE" => StatementKind.Delete,
            "INSERT" => StatementKind.Insert,
            _ => null
        };
    }

    /// <summary>
    /// Returns the leading keyword of a statement (uppercased), skipping
    /// whitespace, "--" line comments and block comments.
    /// </summary>
    public static string GetLeadingKeyword(string statement)
    {
        var span = statement.AsSpan().TrimStart();
        while (true)
        {
            if (span.StartsWith("--", StringComparison.Ordinal))
            {
                var nl = span.IndexOf('\n');
                if (nl < 0)
                    return string.Empty;
                span = span[(nl + 1)..].TrimStart();
                continue;
            }
            if (span.StartsWith("/*", StringComparison.Ordinal))
            {
                var end = span.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                    return string.Empty;
                span = span[(end + 2)..].TrimStart();
                continue;
            }
            break;
        }

        var len = 0;
        while (len < span.Length && char.IsLetter(span[len]))
            len++;
        return len == 0 ? string.Empty : span[..len].ToString().ToUpperInvariant();
    }

    /// <summary>
    /// Builds a single WHERE clause covering every row a set of same-table
    /// mutation statements can touch: the individual WHERE conditions are
    /// OR'ed together. Returns null when any mutation has no WHERE (meaning
    /// the whole table is affected) or when the list is empty.
    /// </summary>
    public static string? BuildCombinedWhereClause(IReadOnlyList<string> mutationStatements)
    {
        var wheres = new List<string>();
        foreach (var statement in mutationStatements)
        {
            var where = SqlStatementParser.ExtractWhereClause(statement);
            if (where is null)
                return null; // a mutation without WHERE touches the whole table
            wheres.Add(where["WHERE".Length..].Trim());
        }

        if (wheres.Count == 0)
            return null;

        return wheres.Count == 1
            ? $"WHERE {wheres[0]}"
            : $"WHERE ({string.Join(") OR (", wheres)})";
    }

    /// <summary>
    /// Chooses the fallback row-identity columns for a table without a declared
    /// primary key: every column except types without a plain equality operator
    /// (json/xml) or large binary payloads (bytea).
    /// </summary>
    public static IReadOnlyList<string> SelectFallbackKeyColumns(
        IEnumerable<(string Name, string DataType)> columns)
    {
        return columns
            .Where(c => !FallbackExcludedTypes.Contains(c.DataType))
            .Select(c => c.Name)
            .ToList();
    }

    /// <summary>
    /// Renders a .NET value as a PostgreSQL literal: null/DBNull → NULL, strings →
    /// '...' with '' escaping, bool → TRUE/FALSE, numerics → InvariantCulture,
    /// date/time → 'yyyy-MM-dd HH:mm:ss.fffffff', byte[] → '\x..'::bytea,
    /// everything else → quoted string.
    /// </summary>
    public static string RenderSqlLiteral(object? value)
    {
        return value switch
        {
            null or DBNull => "NULL",
            JsonElement element => RenderSqlLiteral(MaterializeJsonElement(element)),
            string s => $"'{s.Replace("'", "''")}'",
            char c => $"'{c.ToString().Replace("'", "''")}'",
            bool b => b ? "TRUE" : "FALSE",
            byte or sbyte or short or ushort or int or uint or long or ulong =>
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double dbl => dbl.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fffffff}'",
            DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss.fffffffzzz}'",
            DateOnly d => $"'{d:yyyy-MM-dd}'",
            TimeOnly t => $"'{t:HH:mm:ss.fffffff}'",
            TimeSpan ts => $"'{ts:c}'::interval",
            byte[] bytes => $"'\\x{Convert.ToHexString(bytes)}'::bytea",
            Guid g => $"'{g}'",
            _ => $"'{(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Replace("'", "''")}'"
        };
    }

    /// <summary>
    /// Serializes captured rows to a JSON array of objects. Sql-typed values are
    /// normalized: DBNull → null, byte[] → base64, DateTime/DateTimeOffset →
    /// ISO "o", Guid → string, JsonElement → embedded verbatim.
    /// </summary>
    public static string SerializeRows(IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                foreach (var (name, value) in row)
                {
                    writer.WritePropertyName(name);
                    WriteJsonValue(writer, value);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Deserializes a rows JSON array back into dictionaries. Scalar values are
    /// kept as <see cref="JsonElement"/> (detached from the document) so the
    /// original SQL-ish typing is preserved for literal rendering.
    /// </summary>
    public static List<Dictionary<string, object?>> DeserializeRows(string rowsJson)
    {
        var result = new List<Dictionary<string, object?>>();
        if (string.IsNullOrWhiteSpace(rowsJson))
            return result;

        using var document = JsonDocument.Parse(rowsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                    row[property.Name] = property.Value.Clone();
            }
            result.Add(row);
        }
        return result;
    }

    /// <summary>
    /// Generates the inverse of an UPDATE fix: one UPDATE per captured row
    /// restoring every column to its old value, keyed by primary key.
    /// </summary>
    public static string BuildUpdateInverse(
        string table, IReadOnlyList<string> pkColumns,
        IReadOnlyList<Dictionary<string, object?>> beforeRows)
    {
        var sb = new StringBuilder();
        foreach (var row in beforeRows)
        {
            var assignments = string.Join(", ",
                row.Select(kv => $"{QuoteIdentifier(kv.Key)} = {RenderSqlLiteral(kv.Value)}"));
            sb.Append("UPDATE ").Append(table)
              .Append(" SET ").Append(assignments)
              .Append(" WHERE ").Append(BuildPkPredicate(pkColumns, row))
              .Append(";\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Generates the inverse of a DELETE fix: one INSERT per captured row,
    /// adding OVERRIDING SYSTEM VALUE when the table has a GENERATED ALWAYS
    /// AS IDENTITY column (the only identity flavor that rejects explicit values).
    /// </summary>
    public static string BuildDeleteInverse(
        string table, IReadOnlyList<string> pkColumns,
        IReadOnlyList<Dictionary<string, object?>> beforeRows, bool hasIdentityColumn)
    {
        if (beforeRows.Count == 0)
            return string.Empty;

        // Union of column names across all captured rows (first-row order first).
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in beforeRows)
        {
            foreach (var key in row.Keys)
            {
                if (seen.Add(key))
                    columns.Add(key);
            }
        }

        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));
        var sb = new StringBuilder();

        foreach (var row in beforeRows)
        {
            var literals = string.Join(", ",
                columns.Select(c => RenderSqlLiteral(row.TryGetValue(c, out var v) ? v : null)));
            sb.Append("INSERT INTO ").Append(table)
              .Append(" (").Append(columnList).Append(')')
              .Append(hasIdentityColumn ? " OVERRIDING SYSTEM VALUE" : string.Empty)
              .Append(" VALUES (").Append(literals).Append(");\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates the inverse of an INSERT fix: one DELETE per inserted row,
    /// keyed by the literal primary-key values parsed from the INSERT.
    /// </summary>
    public static string BuildInsertInverse(
        string table, IReadOnlyList<string> pkColumns,
        IReadOnlyList<Dictionary<string, object?>> insertedPkRows)
    {
        var sb = new StringBuilder();
        foreach (var pkRow in insertedPkRows)
        {
            sb.Append("DELETE FROM ").Append(table)
              .Append(" WHERE ").Append(BuildPkPredicate(pkColumns, pkRow))
              .Append(";\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds a "pk = literal AND ..." predicate for a single row identity.
    /// Null values render as "IS NULL".
    /// </summary>
    public static string BuildPkPredicate(
        IReadOnlyList<string> pkColumns, IReadOnlyDictionary<string, object?> row)
    {
        var parts = pkColumns.Select(column =>
        {
            row.TryGetValue(column, out var value);
            return value is null or DBNull
                ? $"{QuoteIdentifier(column)} IS NULL"
                : $"{QuoteIdentifier(column)} = {RenderSqlLiteral(value)}";
        });
        return "(" + string.Join(" AND ", parts) + ")";
    }

    /// <summary>
    /// Builds a WHERE clause locating a set of rows by their primary-key
    /// predicates (OR'ed). Returns null when there are no rows.
    /// </summary>
    public static string? BuildRowLookupWhere(
        IReadOnlyList<string> pkColumns, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        var predicates = rows.Select(r => BuildPkPredicate(pkColumns, r)).ToList();
        return predicates.Count == 0 ? null : "WHERE " + string.Join(" OR ", predicates);
    }

    /// <summary>
    /// Parses an <c>INSERT INTO table (cols) VALUES (...), (...)</c> statement
    /// into its column list and literal value rows. Throws for
    /// INSERT ... SELECT / EXEC / DEFAULT or unsupported literal expressions.
    /// </summary>
    public static ParsedInsert ParseInsertStatement(string statement)
    {
        var head = InsertHeadPattern.Match(statement);
        if (!head.Success)
            throw new InvalidOperationException(
                $"Statement is not an INSERT INTO with an explicit target table: '{Truncate(statement, 120)}'.");

        var index = SkipWhitespace(statement, head.Index + head.Length);

        // Optional explicit column list — required to map values to PK columns.
        var columns = new List<string>();
        if (index < statement.Length && statement[index] == '(')
        {
            var inner = ExtractParenthesized(statement, index, out var endIndex);
            columns = SplitTopLevelCommas(inner)
                .Select(c => c.Trim().Trim('[', ']', '"'))
                .Where(c => c.Length > 0)
                .ToList();
            index = SkipWhitespace(statement, endIndex + 1);
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException(
                "INSERT statements without an explicit column list are not supported " +
                "for row-level snapshots; cannot map values to primary key columns.");
        }

        var keywordMatch = WordPattern.Match(statement, index);
        if (!keywordMatch.Success
            || !keywordMatch.Groups["word"].Value.Equals("VALUES", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Only INSERT ... VALUES with literal values is supported for row-level snapshots; " +
                "INSERT ... SELECT/EXEC/DEFAULT cannot be reverted safely.");
        }
        index = keywordMatch.Index + keywordMatch.Length;

        var rows = new List<IReadOnlyList<object?>>();
        while (true)
        {
            index = SkipWhitespace(statement, index);
            if (index >= statement.Length || statement[index] != '(')
                break;

            var inner = ExtractParenthesized(statement, index, out var endIndex);
            var tokens = SplitTopLevelCommas(inner);
            if (tokens.Count != columns.Count)
            {
                throw new InvalidOperationException(
                    $"INSERT row has {tokens.Count} value(s) but the column list has {columns.Count}; " +
                    "malformed INSERT statement.");
            }

            rows.Add(tokens.Select(ParseLiteral).ToList());
            index = SkipWhitespace(statement, endIndex + 1);

            if (index < statement.Length && statement[index] == ',')
            {
                index++;
                continue;
            }
            break;
        }

        if (rows.Count == 0)
            throw new InvalidOperationException("INSERT ... VALUES statement contains no row literals.");

        return new ParsedInsert(columns, rows);
    }

    /// <summary>Truncates a string for safe inclusion in error messages.</summary>
    public static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    // ─── Private helpers ────────────────────────────────────────────────────

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                writer.WriteNullValue();
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt.ToString("o", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("o", CultureInfo.InvariantCulture));
                break;
            case DateOnly d:
                writer.WriteStringValue(d.ToString("o", CultureInfo.InvariantCulture));
                break;
            case TimeOnly t:
                writer.WriteStringValue(t.ToString("o", CultureInfo.InvariantCulture));
                break;
            case Guid g:
                writer.WriteStringValue(g);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case uint ui:
                writer.WriteNumberValue(ui);
                break;
            case ulong ul:
                writer.WriteNumberValue(ul);
                break;
            case short or ushort or byte or sbyte:
                writer.WriteNumberValue(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case double dbl:
                writer.WriteNumberValue(dbl);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            default:
                writer.WriteStringValue(
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }
    }

    private static object? MaterializeJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l)
            ? l
            : element.TryGetDecimal(out var m) ? m : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => element.GetRawText()
    };

    private static int SkipWhitespace(string sql, int index)
    {
        while (index < sql.Length && char.IsWhiteSpace(sql[index]))
            index++;
        return index;
    }

    /// <summary>
    /// Returns the content between the parentheses starting at
    /// <paramref name="startIndex"/> (which must point at '('), honoring nested
    /// parentheses and single-quoted literals with '' escapes.
    /// </summary>
    private static string ExtractParenthesized(string sql, int startIndex, out int endIndex)
    {
        var depth = 0;
        var inLiteral = false;
        for (var i = startIndex; i < sql.Length; i++)
        {
            var c = sql[i];
            if (inLiteral)
            {
                if (c == '\'')
                {
                    if (i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        i++;
                        continue;
                    }
                    inLiteral = false;
                }
                continue;
            }

            if (c == '\'')
            {
                inLiteral = true;
                continue;
            }
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    endIndex = i;
                    return sql[(startIndex + 1)..i];
                }
            }
        }

        throw new InvalidOperationException("Unbalanced parentheses in INSERT statement.");
    }

    /// <summary>
    /// Splits a list body on top-level commas, honoring nested parentheses and
    /// single-quoted literals with '' escapes.
    /// </summary>
    private static List<string> SplitTopLevelCommas(string inner)
    {
        var parts = new List<string>();
        var depth = 0;
        var inLiteral = false;
        var start = 0;

        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (inLiteral)
            {
                if (c == '\'')
                {
                    if (i + 1 < inner.Length && inner[i + 1] == '\'')
                    {
                        i++;
                        continue;
                    }
                    inLiteral = false;
                }
                continue;
            }

            if (c == '\'')
            {
                inLiteral = true;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(inner[start..i]);
                start = i + 1;
            }
        }

        parts.Add(inner[start..]);
        return parts;
    }

    /// <summary>
    /// Parses a single literal token from an INSERT ... VALUES row: NULL,
    /// TRUE/FALSE, 'string' / N'string' / E'string' (with '' escapes),
    /// integer/decimal numbers, '\x..' bytea hex literals and 0x binary literals.
    /// Anything else is rejected.
    /// </summary>
    private static object? ParseLiteral(string token)
    {
        var t = token.Trim();
        if (t.Length == 0)
            throw new InvalidOperationException("Empty value in INSERT ... VALUES row.");

        if (t.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return null;

        if (t.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
            return true;

        if (t.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            return false;

        if (t.Length >= 3 && (t.StartsWith("N'", StringComparison.OrdinalIgnoreCase)
                              || t.StartsWith("E'", StringComparison.OrdinalIgnoreCase)) && t.EndsWith('\''))
            return t[2..^1].Replace("''", "'");

        if (t.Length >= 2 && t.StartsWith('\'') && t.EndsWith('\''))
        {
            var inner = t[1..^1];
            // PostgreSQL bytea hex literal: '\xDEADBEEF' (optionally ::bytea-cast).
            if (inner.StartsWith("\\x", StringComparison.Ordinal))
            {
                try
                {
                    return Convert.FromHexString(inner[2..]);
                }
                catch (FormatException ex)
                {
                    throw new InvalidOperationException(
                        $"Invalid bytea hex literal in INSERT ... VALUES: '{t}'.", ex);
                }
            }
            return inner.Replace("''", "'");
        }

        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Convert.FromHexString(t[2..]);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"Invalid hex literal in INSERT ... VALUES: '{t}'.", ex);
            }
        }

        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return l;

        if (decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out var m))
            return m;

        throw new InvalidOperationException(
            $"Unsupported literal/expression in INSERT ... VALUES: '{Truncate(t, 60)}'.");
    }
}

/// <summary>Result of parsing a literal INSERT ... VALUES statement.</summary>
public sealed record ParsedInsert(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows);
