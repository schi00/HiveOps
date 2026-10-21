using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Logging;
using HiveOps.Application;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Persistence;

// Test seam: unit tests exercise the internal matching/parameter helpers and
// swap IPlaybookCommandExecutor for a fake so gate logic runs without a live database.
[assembly: InternalsVisibleTo("HiveOps.UnitTests")]

namespace HiveOps.Infrastructure.Support;

/// <summary>
/// Executes deterministic, pre-approved tenant runbooks (L1/L2 operational
/// actions). Template placeholders (<c>@paramName</c>) are always bound through
/// real <see cref="NpgsqlParameter"/> instances — never interpolated into SQL text.
/// </summary>
public sealed class PlaybookRunner : IPlaybookRunner
{
    /// <summary>Minimum keyword hits required for a symptom-based match.</summary>
    internal const int MinimumKeywordHits = 2;

    /// <summary>Extracts "key: value" / "key=value" / "key#value" pairs from incident text.</summary>
    private static readonly Regex ParameterExtractionRegex = new(
        @"(?<key>\w+)\s*[:=#]\s*(?<val>[\w\-\.]+)",
        RegexOptions.Compiled);

    /// <summary>Splits TriggerSymptoms into word tokens.</summary>
    private static readonly Regex SymptomTokenRegex = new(
        @"[\p{L}\p{N}_]+",
        RegexOptions.Compiled);

    /// <summary>Finds <c>@paramName</c> placeholders (excludes @@system functions).</summary>
    private static readonly Regex TemplateParameterRegex = new(
        @"(?<!@)@(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly IDynamicConnectionStringResolver _connectionStringResolver;
    private readonly ILogger<PlaybookRunner> _logger;

    /// <summary>
    /// Replaceable SQL executor. Production uses <see cref="NpgsqlPlaybookCommandExecutor"/>;
    /// unit tests inject a fake to exercise the safety gates without a database.
    /// </summary>
    internal IPlaybookCommandExecutor CommandExecutor { get; set; } = new NpgsqlPlaybookCommandExecutor();

    public PlaybookRunner(
        AppDbContext db,
        IDynamicConnectionStringResolver connectionStringResolver,
        ILogger<PlaybookRunner> logger)
    {
        _db = db;
        _connectionStringResolver = connectionStringResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TenantPlaybook?> FindMatchingPlaybookAsync(
        Guid tenantId, Incident incident, string? preferredCode, CancellationToken ct = default)
    {
        // Explicit TenantId predicate + IgnoreQueryFilters: the runner can be invoked from
        // background flows where TenantContext is not resolved, so ambient query filters
        // cannot be relied on. Filtering by the parameter keeps tenant isolation intact.
        var candidates = await _db.TenantPlaybooks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.IsActive)
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(preferredCode))
        {
            var exact = candidates.FirstOrDefault(
                p => string.Equals(p.Code, preferredCode, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                _logger.LogInformation(
                    "Tenant {TenantId} | Incident {IncidentId} | playbook {Code} matched by preferred code.",
                    tenantId, incident.Id, exact.Code);
                return exact;
            }

            _logger.LogInformation(
                "Tenant {TenantId} | Incident {IncidentId} | preferred playbook code {Code} not found; falling back to symptom matching.",
                tenantId, incident.Id, preferredCode);
        }

        var incidentText = $"{incident.Title} {incident.Description}";
        TenantPlaybook? best = null;
        var bestHits = 0;
        foreach (var playbook in candidates)
        {
            var hits = ScoreSymptomOverlap(playbook.TriggerSymptoms, incidentText);
            if (hits > bestHits)
            {
                best = playbook;
                bestHits = hits;
            }
        }

        if (best is null || bestHits < MinimumKeywordHits)
        {
            _logger.LogInformation(
                "Tenant {TenantId} | Incident {IncidentId} | no playbook matched (best score {Hits}, minimum {Minimum}).",
                tenantId, incident.Id, bestHits, MinimumKeywordHits);
            return null;
        }

        _logger.LogInformation(
            "Tenant {TenantId} | Incident {IncidentId} | playbook {Code} matched by symptom overlap ({Hits} hits).",
            tenantId, incident.Id, best.Code, bestHits);
        return best;
    }

    /// <inheritdoc />
    public async Task<PlaybookExecutionResult> ExecuteAsync(
        TenantPlaybook playbook, Incident incident,
        IReadOnlyDictionary<string, string> extractedArgs, CancellationToken ct = default)
    {
        try
        {
            // Gate: action type.
            var isStoredProcedure = string.Equals(playbook.ActionType, "StoredProcedure", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(playbook.ActionType, "HttpWebhook", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Tenant {TenantId} | playbook {Code} uses HttpWebhook actions, which are not yet supported.",
                    playbook.TenantId, playbook.Code);
                return new PlaybookExecutionResult(false, false, playbook.Code, 0,
                    "HttpWebhook playbook was not executed.", "HttpWebhook playbooks not yet supported");
            }

            if (!isStoredProcedure && !string.Equals(playbook.ActionType, "SqlTemplate", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Tenant {TenantId} | playbook {Code} has unknown action type {ActionType}.",
                    playbook.TenantId, playbook.Code, playbook.ActionType);
                return new PlaybookExecutionResult(false, false, playbook.Code, 0,
                    "Playbook was not executed.", $"Unsupported action type: {playbook.ActionType}");
            }

            // Gate: required parameters (explicit args win; incident text is the fallback source).
            var availableParameters = MergeParameters(incident, extractedArgs);
            var required = ParseRequiredParameters(playbook.RequiredParametersJson);
            var missing = required.Where(r => !availableParameters.ContainsKey(r)).ToList();
            if (missing.Count > 0)
            {
                _logger.LogWarning(
                    "Tenant {TenantId} | playbook {Code} blocked: missing required parameters {Missing}.",
                    playbook.TenantId, playbook.Code, string.Join(", ", missing));
                return new PlaybookExecutionResult(false, false, playbook.Code, 0,
                    $"missing params: {string.Join(", ", missing)}", null);
            }

            // Gate: template safety (skipped for StoredProcedure — the payload is a proc
            // name, not SQL text; MaxAffectedRows and RequiresApproval still apply).
            if (!isStoredProcedure)
            {
                var rejection = SafetyValidator.GetRejectionReason(playbook.ActionPayloadTemplate);
                if (rejection is not null)
                {
                    _logger.LogWarning(
                        "Tenant {TenantId} | playbook {Code} template rejected by safety validator: {Reason}",
                        playbook.TenantId, playbook.Code, rejection);
                    return new PlaybookExecutionResult(false, false, playbook.Code, 0,
                        $"Template rejected by safety validator: {rejection}", rejection);
                }
            }

            if (!TryBuildCommand(playbook, required, availableParameters, isStoredProcedure, out var command, out var bindError))
            {
                _logger.LogWarning(
                    "Tenant {TenantId} | playbook {Code} parameter binding failed: {Error}",
                    playbook.TenantId, playbook.Code, bindError);
                return new PlaybookExecutionResult(false, false, playbook.Code, 0,
                    $"Parameter binding failed: {bindError}", bindError);
            }

            // Gate: tenant database connectivity.
            string connectionString;
            try
            {
                await _connectionStringResolver.WarmCacheAsync(playbook.TenantId, ct);
                connectionString = _connectionStringResolver.Resolve(playbook.TenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Tenant {TenantId} | playbook {Code} could not resolve the tenant connection string.",
                    playbook.TenantId, playbook.Code);
                return new PlaybookExecutionResult(false, false, playbook.Code, 0,
                    "Tenant database connection unavailable.", ex.Message);
            }

            // Dry-run: execute inside a transaction and roll back to measure the blast radius.
            int dryRunRows;
            try
            {
                dryRunRows = await CommandExecutor.DryRunAsync(connectionString, command, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Tenant {TenantId} | playbook {Code} dry-run failed.",
                    playbook.TenantId, playbook.Code);
                return new PlaybookExecutionResult(false, false, playbook.Code, 0,
                    "Dry-run failed; playbook was not executed.", ex.Message);
            }

            // Gate: blast radius ceiling.
            if (dryRunRows > playbook.MaxAffectedRows)
            {
                _logger.LogWarning(
                    "Tenant {TenantId} | playbook {Code} dry-run would affect {Rows} rows, exceeding MaxAffectedRows {Max}; held for review.",
                    playbook.TenantId, playbook.Code, dryRunRows, playbook.MaxAffectedRows);
                return new PlaybookExecutionResult(false, false, playbook.Code, dryRunRows,
                    $"would affect {dryRunRows} rows, max {playbook.MaxAffectedRows} — held for review", null);
            }

            // Gate: human approval. The playbook is prepared and validated but never commits.
            if (playbook.RequiresApproval)
            {
                _logger.LogInformation(
                    "Tenant {TenantId} | playbook {Code} prepared ({Rows} rows in dry-run); awaiting approval.",
                    playbook.TenantId, playbook.Code, dryRunRows);
                return new PlaybookExecutionResult(false, true, playbook.Code, dryRunRows,
                    $"prepared, awaiting approval ({dryRunRows} rows would be affected)", null);
            }

            // Commit path: real execution inside a transaction, then optional verification query.
            PlaybookCommitOutcome outcome;
            try
            {
                outcome = await CommandExecutor.ExecuteAndCommitAsync(
                    connectionString, command, playbook.VerificationSqlQuery, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Tenant {TenantId} | playbook {Code} execution failed.",
                    playbook.TenantId, playbook.Code);
                return new PlaybookExecutionResult(true, false, playbook.Code, 0,
                    "Playbook execution failed.", ex.Message);
            }

            var summary = $"executed, {outcome.RowsAffected} row(s) affected";
            if (!string.IsNullOrWhiteSpace(playbook.VerificationSqlQuery))
            {
                if (outcome.VerificationError is not null)
                    summary += $"; verification query failed: {outcome.VerificationError}";
                else if (outcome.VerificationAnomalyRows == 0)
                    summary += "; verification passed (0 anomaly rows)";
                else
                    summary += $"; verification returned {outcome.VerificationAnomalyRows} anomaly row(s)";
            }

            _logger.LogInformation(
                "Tenant {TenantId} | playbook {Code} executed for incident {IncidentId}: {Summary}",
                playbook.TenantId, playbook.Code, incident.Id, summary);
            return new PlaybookExecutionResult(true, true, playbook.Code, outcome.RowsAffected, summary, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Tenant {TenantId} | playbook {Code} unexpected execution failure.",
                playbook.TenantId, playbook.Code);
            return new PlaybookExecutionResult(false, false, playbook.Code, 0,
                "Unexpected playbook execution failure.", ex.Message);
        }
    }

    /// <summary>
    /// Extracts <c>key: value</c>, <c>key=value</c> and <c>key#value</c> pairs from free
    /// incident text. First occurrence wins; keys are matched case-insensitively.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ExtractParametersFromText(string? text)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return parameters;

        foreach (Match match in ParameterExtractionRegex.Matches(text))
        {
            var key = match.Groups["key"].Value;
            if (!parameters.ContainsKey(key))
                parameters[key] = match.Groups["val"].Value;
        }

        return parameters;
    }

    /// <summary>
    /// Parses <see cref="TenantPlaybook.RequiredParametersJson"/> (a JSON array of strings).
    /// Malformed payloads yield an empty list — the controller validates the shape at write time.
    /// </summary>
    internal static IReadOnlyList<string> ParseRequiredParameters(string? requiredParametersJson)
    {
        if (string.IsNullOrWhiteSpace(requiredParametersJson))
            return [];

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(requiredParametersJson);
            return parsed ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Counts how many distinct TriggerSymptoms tokens (length &gt; 3) appear inside the
    /// incident text, case-insensitively.
    /// </summary>
    internal static int ScoreSymptomOverlap(string? triggerSymptoms, string? incidentText)
    {
        if (string.IsNullOrWhiteSpace(triggerSymptoms) || string.IsNullOrWhiteSpace(incidentText))
            return 0;

        var tokens = SymptomTokenRegex.Matches(triggerSymptoms)
            .Select(m => m.Value)
            .Where(t => t.Length > 3)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var hits = 0;
        foreach (var token in tokens)
        {
            if (incidentText.Contains(token, StringComparison.OrdinalIgnoreCase))
                hits++;
        }

        return hits;
    }

    /// <summary>Lists the <c>@paramName</c> placeholders declared by a template.</summary>
    internal static IReadOnlyList<string> ExtractTemplateParameterNames(string template)
    {
        return TemplateParameterRegex.Matches(template)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Merges explicitly extracted args with regex-extracted incident parameters.
    /// Explicit args take precedence.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> MergeParameters(
        Incident incident, IReadOnlyDictionary<string, string> extractedArgs)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in ExtractParametersFromText($"{incident.Title} {incident.Description}"))
            merged[kvp.Key] = kvp.Value;
        foreach (var kvp in extractedArgs)
            merged[kvp.Key] = kvp.Value;
        return merged;
    }

    /// <summary>
    /// Builds the executable command. SqlTemplate binds every <c>@placeholder</c> found in the
    /// template; StoredProcedure binds the declared required parameters (the proc's contract).
    /// All values become real <see cref="NpgsqlParameter"/> instances downstream.
    /// </summary>
    private static bool TryBuildCommand(
        TenantPlaybook playbook,
        IReadOnlyList<string> required,
        IReadOnlyDictionary<string, string> availableParameters,
        bool isStoredProcedure,
        out PreparedPlaybookCommand command,
        out string? error)
    {
        command = null!;
        error = null;

        var namesToBind = isStoredProcedure
            ? required
            : ExtractTemplateParameterNames(playbook.ActionPayloadTemplate);

        var bound = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in namesToBind)
        {
            if (!availableParameters.TryGetValue(name, out var value))
            {
                error = isStoredProcedure
                    ? $"required parameter '{name}' has no value"
                    : $"template placeholder '@{name}' has no bound value";
                return false;
            }
            bound[name] = value;
        }

        command = new PreparedPlaybookCommand(
            playbook.ActionPayloadTemplate.Trim(), isStoredProcedure, bound);
        return true;
    }
}

/// <summary>A bound playbook command ready to run against the tenant database.</summary>
internal sealed record PreparedPlaybookCommand(
    string CommandText,
    bool IsStoredProcedure,
    IReadOnlyDictionary<string, string> Parameters);

/// <summary>Result of the committed execution plus the optional verification outcome.</summary>
internal sealed record PlaybookCommitOutcome(
    int RowsAffected,
    int? VerificationAnomalyRows,
    string? VerificationError);

/// <summary>
/// Narrow seam over raw SQL execution so unit tests can exercise the safety gates
/// without a live PostgreSQL instance.
/// </summary>
internal interface IPlaybookCommandExecutor
{
    /// <summary>Runs the command inside a transaction and rolls back; returns the rows that would be affected.</summary>
    Task<int> DryRunAsync(string connectionString, PreparedPlaybookCommand command, CancellationToken ct);

    /// <summary>Runs the command inside a transaction, commits, and runs the verification query on the same connection.</summary>
    Task<PlaybookCommitOutcome> ExecuteAndCommitAsync(
        string connectionString, PreparedPlaybookCommand command, string? verificationSql, CancellationToken ct);
}

/// <summary>PostgreSQL implementation of <see cref="IPlaybookCommandExecutor"/>.</summary>
internal sealed class NpgsqlPlaybookCommandExecutor : IPlaybookCommandExecutor
{
    public async Task<int> DryRunAsync(string connectionString, PreparedPlaybookCommand command, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            var rowsAffected = await ExecuteNonQueryAsync(connection, transaction, command, ct);
            await transaction.RollbackAsync(ct);
            return rowsAffected;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { /* best effort */ }
            throw;
        }
    }

    public async Task<PlaybookCommitOutcome> ExecuteAndCommitAsync(
        string connectionString, PreparedPlaybookCommand command, string? verificationSql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        int rowsAffected;
        await using (var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            try
            {
                rowsAffected = await ExecuteNonQueryAsync(connection, transaction, command, ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                try { await transaction.RollbackAsync(CancellationToken.None); } catch { /* best effort */ }
                throw;
            }
        }

        // Verification runs on the same connection after the commit.
        int? anomalyRows = null;
        string? verificationError = null;
        if (!string.IsNullOrWhiteSpace(verificationSql))
        {
            try
            {
                await using var verifyCommand = connection.CreateCommand();
                verifyCommand.CommandText = verificationSql;
                await using var reader = await verifyCommand.ExecuteReaderAsync(ct);
                var count = 0;
                while (await reader.ReadAsync(ct))
                    count++;
                anomalyRows = count;
            }
            catch (Exception ex)
            {
                verificationError = ex.Message;
            }
        }

        return new PlaybookCommitOutcome(rowsAffected, anomalyRows, verificationError);
    }

    private static async Task<int> ExecuteNonQueryAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PreparedPlaybookCommand command, CancellationToken ct)
    {
        await using var sqlCommand = connection.CreateCommand();
        sqlCommand.Transaction = transaction;
        sqlCommand.CommandType = command.IsStoredProcedure ? CommandType.StoredProcedure : CommandType.Text;
        sqlCommand.CommandText = command.CommandText;

        // Parameters carry the '@' prefix Npgsql expects; values stay typed as
        // strings and let PostgreSQL convert — avoids mangling codes like "007".
        foreach (var (name, value) in command.Parameters)
            sqlCommand.Parameters.Add(new NpgsqlParameter("@" + name, value));

        return await sqlCommand.ExecuteNonQueryAsync(ct);
    }
}
