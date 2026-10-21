using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Agents.Support;

/// <summary>
/// Multi-source root-cause investigation engine. Before proposing any fix it:
/// 1. Discovers the relevant tenant database schema (read-only).
/// 2. Correlates file/class tokens in the incident with recent git commits.
/// 3. Collects recent application log entries from the tenant database.
/// 4. Asks the LLM for up to 3 diagnostic SELECT queries, validates each with
///    <see cref="HiveOps.Application.SafetyValidator"/> and executes them read-only.
/// 5. Feeds all gathered evidence back to the LLM for a structured hypothesis:
///    recommended track, matched playbook code and a verification SELECT that
///    must return zero anomaly rows once the problem is fixed.
///
/// Safety guarantees:
/// - Only validated SELECT statements are ever executed against the tenant DB.
/// - Every external dependency degrades gracefully (empty evidence) — the
///   service never throws for missing schema, logs or git history.
/// - Every prompt/response and executed query is persisted in DiagnosticLog.
/// </summary>
public sealed class RootCauseHypothesisService : IRootCauseHypothesisService
{
    private const int MaxDiagnosticQueries = 3;
    private const int MaxSchemaCharsForPrompt = 6000;
    private const int MaxEvidenceCharsForPrompt = 4000;
    private const int MaxGitPathCandidates = 20;
    private const int MaxRenderedLogChars = 4000;
    private static readonly TimeSpan DefaultLogLookback = TimeSpan.FromHours(24);

    private static readonly Regex SelectStartPattern = new(
        @"^\s*SELECT\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Same read-only gate as TargetDatabaseDiagnosticService: a "SELECT ...; DELETE ..."
    // script still passes SafetyValidator (governed DELETE is legal there) but must
    // never reach the tenant database from this engine.
    private static readonly Regex ForbiddenSqlPattern = new(
        @"\b(INSERT|UPDATE|DELETE|DROP|ALTER|TRUNCATE|EXEC|EXECUTE|MERGE|GRANT)\b|\bINTO\s+\w+\s+FROM\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FilePathTokenPattern = new(
        @"[\w.\-/\\]+\.(?:cs|cshtml|razor|ts|tsx|js|sql)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TypeNameTokenPattern = new(
        @"\b[A-Z][A-Za-z0-9_]*(?:Service|Controller|Repository|Handler|Manager|Provider|Factory|Plugin|Exception|Worker|Job|Validator)\b",
        RegexOptions.Compiled);

    private static readonly HashSet<string> ValidTracks = new(StringComparer.OrdinalIgnoreCase)
    {
        "Playbook", "DatabaseFix", "CodeFix", "Escalate"
    };

    private readonly KernelFactory _kernelFactory;
    private readonly AppDbContext _db;
    private readonly ITenantConfigService _tenantConfigService;
    private readonly IOptions<HiveOpsDeploymentOptions> _deploymentOptions;
    private readonly ITargetDatabaseDiagnosticService _targetDb;
    private readonly ITargetLogCollector _logCollector;
    private readonly IGitService _gitService;
    private readonly ILogger<RootCauseHypothesisService> _logger;
    private readonly Func<Guid, string, string, CancellationToken, Task<string>> _chatCompletion;

    public RootCauseHypothesisService(
        KernelFactory kernelFactory,
        AppDbContext db,
        ITenantConfigService tenantConfigService,
        IOptions<HiveOpsDeploymentOptions> deploymentOptions,
        ITargetDatabaseDiagnosticService targetDb,
        ITargetLogCollector logCollector,
        IGitService gitService,
        ILogger<RootCauseHypothesisService> logger)
        : this(kernelFactory, db, tenantConfigService, deploymentOptions,
            targetDb, logCollector, gitService, logger, chatCompletion: null)
    {
    }

    /// <summary>
    /// Test seam: <paramref name="chatCompletion"/> replaces the Semantic Kernel
    /// round-trip so deterministic validation logic can be unit-tested.
    /// </summary>
    internal RootCauseHypothesisService(
        KernelFactory kernelFactory,
        AppDbContext db,
        ITenantConfigService tenantConfigService,
        IOptions<HiveOpsDeploymentOptions> deploymentOptions,
        ITargetDatabaseDiagnosticService targetDb,
        ITargetLogCollector logCollector,
        IGitService gitService,
        ILogger<RootCauseHypothesisService> logger,
        Func<Guid, string, string, CancellationToken, Task<string>>? chatCompletion)
    {
        _kernelFactory = kernelFactory;
        _db = db;
        _tenantConfigService = tenantConfigService;
        _deploymentOptions = deploymentOptions;
        _targetDb = targetDb;
        _logCollector = logCollector;
        _gitService = gitService;
        _logger = logger;
        _chatCompletion = chatCompletion ?? CompleteChatAsync;
    }

    /// <inheritdoc />
    public async Task<RootCauseBundle> InvestigateAsync(Incident incident, CancellationToken ct = default)
    {
        var tenantId = incident.TenantId;
        var incidentText = $"{incident.Title}\n{incident.Description}";

        // (a) Relevant tenant schema — best effort.
        var schemaDdl = string.Empty;
        try
        {
            var schema = await _targetDb.GetRelevantSchemaAsync(tenantId, incidentText, ct);
            schemaDdl = schema.SchemaDdl ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Schema discovery failed for tenant {TenantId}; continuing without DDL.", tenantId);
        }

        // (b) Recent commits touching suspect files — best effort.
        var commits = await TryGetRecentCommitsAsync(incident, incidentText, ct);

        // (c) Recent application log entries — best effort.
        var logs = await TryCollectApplicationLogsAsync(incident, ct);

        // (d) LLM-suggested diagnostic queries, validated and executed read-only.
        var databaseEvidence = await RunSuggestedDiagnosticsAsync(incident, schemaDdl, ct);

        // (e) Structured hypothesis from all gathered evidence.
        var hypothesis = await DeriveHypothesisAsync(
            incident, schemaDdl, databaseEvidence, logs, commits, ct);

        return new RootCauseBundle(
            incident.Id,
            hypothesis.SummaryHypothesis,
            hypothesis.Confidence,
            hypothesis.RecommendedTrack,
            hypothesis.MatchedPlaybookCode,
            hypothesis.VerificationSqlQuery,
            databaseEvidence,
            commits,
            logs,
            schemaDdl);
    }

    /// <inheritdoc />
    public async Task<bool> VerifyPostFixConditionAsync(
        Guid tenantId, string verificationSqlQuery, CancellationToken ct = default)
    {
        if (!IsValidVerificationQuery(verificationSqlQuery))
            return false;

        try
        {
            var result = await _targetDb.ExecuteReadOnlyQueryAsync(tenantId, verificationSqlQuery, ct);
            return result.Success && result.RowCount == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Post-fix verification query failed for tenant {TenantId}; treating as not verified.", tenantId);
            return false;
        }
    }

    // ─── Evidence gathering ─────────────────────────────────────────────────

    private async Task<IReadOnlyList<GitCommitEvidence>> TryGetRecentCommitsAsync(
        Incident incident, string incidentText, CancellationToken ct)
    {
        var candidates = ExtractGitPathCandidates(incidentText);
        if (candidates.Count == 0)
            return [];

        try
        {
            return await _gitService.GetRecentCommitsForFilesAsync(
                incident.TenantId, candidates, maxCount: 5, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Git commit correlation failed for tenant {TenantId}; continuing without commit evidence.",
                incident.TenantId);
            return [];
        }
    }

    private async Task<IReadOnlyList<AppLogEvidence>> TryCollectApplicationLogsAsync(
        Incident incident, CancellationToken ct)
    {
        try
        {
            return await _logCollector.CollectRecentErrorsAsync(
                incident.TenantId, incident.Title, DefaultLogLookback, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Application log collection failed for tenant {TenantId}; continuing without log evidence.",
                incident.TenantId);
            return [];
        }
    }

    private async Task<IReadOnlyList<DiagnosticQueryEvidence>> RunSuggestedDiagnosticsAsync(
        Incident incident, string schemaDdl, CancellationToken ct)
    {
        var evidence = new List<DiagnosticQueryEvidence>();

        var schemaForPrompt = Truncate(schemaDdl, MaxSchemaCharsForPrompt);
        if (string.IsNullOrWhiteSpace(schemaForPrompt))
            schemaForPrompt = "-- schema unavailable";

        var systemPrompt = $$"""
            You are the HiveOps Root-Cause Diagnostic Planner. Given the target application's database schema (DDL) and an incident report, propose up to {{MaxDiagnosticQueries}} READ-ONLY PostgreSQL diagnostic queries that would gather evidence about the root cause.

            RULES (violation = discard the query):
            - ONLY SELECT statements. Never INSERT, UPDATE, DELETE, DROP, ALTER, TRUNCATE, CALL or UNION.
            - Always cap rows: end the query with LIMIT 20.
            - Only use tables and columns present in the DDL below.
            - Prefer filtering by the concrete ids, statuses or values mentioned in the incident.
            - If no useful query can be derived from the schema, return an empty array.

            TARGET SCHEMA (DDL):
            {{schemaForPrompt}}

            Respond ONLY with valid JSON (no markdown fences):
            { "diagnosticQueries": ["SELECT ... LIMIT 20"] }
            """;

        string raw;
        try
        {
            raw = await _chatCompletion(
                incident.TenantId,
                systemPrompt,
                $"INCIDENT TITLE: {incident.Title}\nINCIDENT DESCRIPTION:\n{incident.Description}",
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Diagnostic query planning failed for incident {IncidentId}; skipping DB evidence.",
                incident.Id);
            return evidence;
        }

        await PersistDiagnosticLogAsync(
            incident, "hypothesis.diagnostic_prompt",
            $"PROMPT:\n{systemPrompt}\n\nRESPONSE:\n{raw}", isSuccess: true, ct);

        foreach (var sql in ParseDiagnosticQueriesJson(raw))
        {
            if (evidence.Count >= MaxDiagnosticQueries)
                break;

            if (!IsCandidateDiagnosticQuery(sql))
            {
                _logger.LogWarning(
                    "Rejected unsafe LLM-suggested diagnostic query for incident {IncidentId}.", incident.Id);
                continue;
            }

            TargetQueryResult result;
            try
            {
                result = await _targetDb.ExecuteReadOnlyQueryAsync(incident.TenantId, sql, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Diagnostic query execution failed for incident {IncidentId}.", incident.Id);
                result = new TargetQueryResult(false, string.Empty, 0, ex.Message);
            }

            evidence.Add(new DiagnosticQueryEvidence(
                sql, result.Success, result.RowCount, result.RenderedResult, result.Error));

            await PersistDiagnosticLogAsync(
                incident, "hypothesis.diagnostic_query",
                $"SQL: {sql}\n\n{Truncate(result.RenderedResult, MaxEvidenceCharsForPrompt)}\n{(result.Success ? "" : $"ERROR: {result.Error}")}",
                result.Success, ct);
        }

        return evidence;
    }

    private async Task<HypothesisOutcome> DeriveHypothesisAsync(
        Incident incident,
        string schemaDdl,
        IReadOnlyList<DiagnosticQueryEvidence> databaseEvidence,
        IReadOnlyList<AppLogEvidence> logs,
        IReadOnlyList<GitCommitEvidence> commits,
        CancellationToken ct)
    {
        var evidenceText = RenderDiagnosticEvidence(databaseEvidence);
        var logText = RenderLogEvidence(logs);
        var commitText = RenderCommitEvidence(commits);
        var schemaText = string.IsNullOrWhiteSpace(schemaDdl)
            ? "(schema unavailable)"
            : Truncate(schemaDdl, MaxSchemaCharsForPrompt);

        var systemPrompt = $$"""
            You are the HiveOps Root-Cause Hypothesis Engine. You receive real evidence collected from the tenant's systems and must produce ONE structured hypothesis about the incident's root cause.

            EVIDENCE — DATABASE DIAGNOSTIC RESULTS:
            {{evidenceText}}

            EVIDENCE — APPLICATION LOG ENTRIES:
            {{logText}}

            EVIDENCE — RECENT COMMITS ON SUSPECT FILES:
            {{commitText}}

            EVIDENCE — RELEVANT SCHEMA:
            {{schemaText}}

            RULES:
            - recommendedTrack must be exactly one of: "Playbook" | "DatabaseFix" | "CodeFix" | "Escalate".
              Use "Escalate" when the evidence is insufficient or the blast radius is unclear.
            - matchedPlaybookCode: the code of a known operational playbook that resolves this class of problem, or null.
            - verificationSqlQuery: a READ-ONLY T-SQL SELECT that returns the ANOMALOUS rows caused by the problem.
              Convention: it MUST return 0 rows once the problem is fixed. Use only tables/columns from the schema above, or null if no reliable verification query exists.
            - confidence: 0.0 to 1.0.

            Respond ONLY with valid JSON (no markdown fences):
            {
              "summaryHypothesis": "...",
              "confidence": 0.0,
              "recommendedTrack": "Playbook|DatabaseFix|CodeFix|Escalate",
              "matchedPlaybookCode": null,
              "verificationSqlQuery": null
            }
            """;

        string raw;
        try
        {
            raw = await _chatCompletion(
                incident.TenantId,
                systemPrompt,
                $"INCIDENT TITLE: {incident.Title}\nINCIDENT DESCRIPTION:\n{incident.Description}",
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Hypothesis synthesis failed for incident {IncidentId}; escalating.", incident.Id);
            return HypothesisOutcome.Fallback;
        }

        var parsed = TryParseHypothesisJson(raw, out var outcome);
        await PersistDiagnosticLogAsync(
            incident, "hypothesis.final",
            $"PROMPT:\n{systemPrompt}\n\nRESPONSE:\n{raw}", parsed, ct);

        return outcome;
    }

    // ─── Deterministic helpers (unit-tested) ────────────────────────────────

    /// <summary>
    /// A suggested diagnostic query is eligible only when it starts with SELECT,
    /// contains no DML/DDL keyword anywhere in the script and passes
    /// <see cref="HiveOps.Application.SafetyValidator.IsSafeSql"/>.
    /// </summary>
    internal static bool IsCandidateDiagnosticQuery(string? sql) =>
        !string.IsNullOrWhiteSpace(sql)
        && SelectStartPattern.IsMatch(sql)
        && !ForbiddenSqlPattern.IsMatch(sql)
        && HiveOps.Application.SafetyValidator.IsSafeSql(sql);

    /// <summary>
    /// The verification query must be a safe read-only SELECT; anything else is rejected.
    /// </summary>
    internal static bool IsValidVerificationQuery(string? sql) =>
        IsCandidateDiagnosticQuery(sql);

    /// <summary>
    /// Extracts workspace-relative path candidates from incident text: path-like
    /// tokens (containing '/' or a source-file extension), their bare filenames,
    /// and "<c>TypeName.cs</c>" guesses for PascalCase type tokens.
    /// </summary>
    internal static IReadOnlyList<string> ExtractGitPathCandidates(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var candidates = new List<string>();
        foreach (Match match in FilePathTokenPattern.Matches(text))
        {
            var token = match.Value.Replace('\\', '/').Trim('.', '/', '\'');
            if (token.Length == 0)
                continue;

            candidates.Add(token);
            var fileName = token[(token.LastIndexOf('/') + 1)..];
            if (!fileName.Equals(token, StringComparison.OrdinalIgnoreCase))
                candidates.Add(fileName);
        }

        foreach (Match match in TypeNameTokenPattern.Matches(text))
            candidates.Add(match.Value + ".cs");

        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxGitPathCandidates)
            .ToList();
    }

    /// <summary>Tolerantly parses the diagnostic-planner JSON into a SQL list (max 3).</summary>
    internal static IReadOnlyList<string> ParseDiagnosticQueriesJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(StripMarkdownJsonFence(raw.Trim()));
            if (!doc.RootElement.TryGetProperty("diagnosticQueries", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return array.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(MaxDiagnosticQueries)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Tolerantly parses the hypothesis JSON. On any parse failure or invalid
    /// track the result degrades to the Escalate fallback; an unsafe or
    /// non-SELECT verificationSqlQuery is nulled out.
    /// </summary>
    internal static bool TryParseHypothesisJson(string? raw, out HypothesisOutcome outcome)
    {
        outcome = HypothesisOutcome.Fallback;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(StripMarkdownJsonFence(raw.Trim()));
            var root = doc.RootElement;

            var summary = root.TryGetProperty("summaryHypothesis", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
            var confidence = root.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var d)
                ? Math.Clamp(d, 0d, 1d)
                : 0d;
            var track = root.TryGetProperty("recommendedTrack", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            var playbookCode = root.TryGetProperty("matchedPlaybookCode", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
            var verification = root.TryGetProperty("verificationSqlQuery", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

            if (!ValidTracks.Contains(track ?? string.Empty))
                track = "Escalate";
            if (!IsValidVerificationQuery(verification))
                verification = null;
            if (string.IsNullOrWhiteSpace(summary))
                summary = HypothesisOutcome.Fallback.SummaryHypothesis;

            outcome = new HypothesisOutcome(summary!, confidence, track!, playbookCode, verification);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string StripMarkdownJsonFence(string raw)
    {
        if (!raw.StartsWith("```", StringComparison.Ordinal))
            return raw;

        var trimmed = raw.Trim();
        var firstNl = trimmed.IndexOf('\n');
        if (firstNl < 0)
            return raw;

        var body = trimmed[(firstNl + 1)..];
        if (body.EndsWith("```", StringComparison.Ordinal))
            body = body[..^3].Trim();

        return body;
    }

    private static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    private static string RenderDiagnosticEvidence(IReadOnlyList<DiagnosticQueryEvidence> evidence)
    {
        if (evidence.Count == 0)
            return "(no diagnostic queries executed)";

        var sb = new StringBuilder();
        foreach (var e in evidence)
        {
            sb.Append("QUERY: ").Append(e.SqlQuery).Append('\n')
              .Append("RESULT (")
              .Append(e.Success ? $"{e.RowCount} rows" : $"error: {e.Error}")
              .Append("):\n")
              .Append(e.RenderedTable)
              .Append("\n---\n");
        }

        return Truncate(sb.ToString(), MaxEvidenceCharsForPrompt);
    }

    private static string RenderLogEvidence(IReadOnlyList<AppLogEvidence> logs)
    {
        if (logs.Count == 0)
            return "(no application log entries collected)";

        var sb = new StringBuilder();
        foreach (var log in logs.Take(20))
        {
            sb.Append('[').Append(log.Timestamp.ToString("u")).Append("] ")
              .Append(log.Level).Append(' ').Append(log.Message);
            if (!string.IsNullOrWhiteSpace(log.ExceptionText))
                sb.Append(" | EX: ").Append(log.ExceptionText);
            sb.Append('\n');
        }

        return Truncate(sb.ToString(), MaxRenderedLogChars);
    }

    private static string RenderCommitEvidence(IReadOnlyList<GitCommitEvidence> commits)
    {
        if (commits.Count == 0)
            return "(no recent commits on suspect files)";

        var sb = new StringBuilder();
        foreach (var c in commits)
        {
            sb.Append(c.CommitHash.AsSpan(0, Math.Min(8, c.CommitHash.Length)))
              .Append(' ').Append(c.CommittedAt.ToString("u"))
              .Append(' ').Append(c.Author).Append(": ").Append(c.Subject)
              .Append(" [").Append(string.Join(", ", c.TouchedFiles.Take(5))).Append("]\n");
        }

        return Truncate(sb.ToString(), MaxRenderedLogChars);
    }

    private async Task<string> CompleteChatAsync(
        Guid tenantId, string systemPrompt, string userMessage, CancellationToken ct)
    {
        var kernel = _kernelFactory.CreateForTenant(tenantId);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(userMessage);

        var exec = await TenantLlmExecutionHelper.GetTriageChatExecutionSettingsAsync(
            tenantId, _tenantConfigService, _deploymentOptions, ct);
        var response = await chat.GetChatMessageContentAsync(history, exec, cancellationToken: ct);
        return (response.Content ?? string.Empty).Trim();
    }

    /// <summary>Best-effort audit row; persistence failures never break the investigation.</summary>
    private async Task PersistDiagnosticLogAsync(
        Incident incident, string stepName, string result, bool isSuccess, CancellationToken ct)
    {
        try
        {
            _db.DiagnosticLogs.Add(new DiagnosticLog
            {
                Id = Guid.NewGuid(),
                TenantId = incident.TenantId,
                IncidentId = incident.Id,
                StepName = stepName,
                Result = result,
                IsSuccess = isSuccess
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Failed to persist diagnostic audit row {StepName} for incident {IncidentId}.",
                stepName, incident.Id);
        }
    }

    /// <summary>Structured hypothesis returned by the second LLM call.</summary>
    internal sealed record HypothesisOutcome(
        string SummaryHypothesis,
        double Confidence,
        string RecommendedTrack,
        string? MatchedPlaybookCode,
        string? VerificationSqlQuery)
    {
        internal static readonly HypothesisOutcome Fallback = new(
            "Evidence could not be synthesized into a root-cause hypothesis; escalating for human review.",
            0d, "Escalate", null, null);
    }
}
