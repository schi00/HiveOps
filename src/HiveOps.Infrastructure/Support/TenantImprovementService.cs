using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Infrastructure.Support;

/// <summary>
/// Manages the per-tenant self-improvement queue (<see cref="TenantImprovementSuggestion"/>).
/// Every agent failure auto-files a suggestion (or bumps OccurrenceCount when the same
/// incident recurs); humans review the queue, optionally ask the service to draft a
/// complete artifact payload via LLM, then convert it into a real
/// <see cref="TenantPlaybook"/> or dismiss it.
///
/// Design notes (same conventions as <see cref="TenantContextPackService"/>):
/// - Tenant isolation: every query predicates on <c>TenantId == tenantId</c> with
///   <c>IgnoreQueryFilters()</c> because the service may run in background flows where
///   TenantContext is unresolved.
/// - <see cref="EnsureForFailedIncidentAsync"/> is hot-path safe: it never throws —
///   failures are logged and return null.
/// - LLM calls go through an injectable chat-completion seam so unit tests never touch
///   Semantic Kernel.
/// - Auto-drafted playbooks start DISABLED (<see cref="TenantPlaybook.IsActive"/> = false),
///   approval-gated (<see cref="TenantPlaybook.RequiresApproval"/> = true) and with a
///   conservative blast-radius ceiling so nothing drafted by the agent can run before
///   human review.
/// </summary>
public sealed class TenantImprovementService : ITenantImprovementService
{
    private const int MaxListResults = 200;
    private const int MaxTitleChars = 120;
    private const int MaxSymptomChars = 800;
    private const int MaxTriggerSymptomChars = 300;
    private const int MaxDescriptionChars = 1000;
    private const int MaxSlugChars = 40;
    private const int MaxKeywords = 12;

    /// <summary>Conservative blast-radius ceiling for auto-drafted playbooks.</summary>
    private const int AutoDraftMaxAffectedRows = 25;

    /// <summary>Kinds that can be converted into a <see cref="TenantPlaybook"/>.</summary>
    private static readonly HashSet<string> PlaybookKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "NewPlaybook", "DataQualityRule"
    };

    /// <summary>Statuses a human reviewer can set via <see cref="SetStatusAsync"/>.</summary>
    private static readonly HashSet<string> DecidedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Suggested", "Dismissed"
    };

    private static readonly HashSet<string> AllowedActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SqlTemplate", "StoredProcedure", "HttpWebhook"
    };

    /// <summary>
    /// Terms pointing at dirty/inconsistent business data (mostly Spanish ERP vocabulary:
    /// stock, precio, fecha, nulo, duplicado, faltante, artículo, remito, proveedor, tabla).
    /// </summary>
    private static readonly Regex DataishTermPattern = new(
        @"stock|precio|fecha|nulo|null|duplicad|faltante|art[ií]culo|remito|proveedor|tabla",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Terms in failure notes indicating a deterministic fix (SQL/script/playbook) was attempted.</summary>
    private static readonly Regex FixAttemptPattern = new(
        @"\b(sql|script|playbook|stored\s*proc|procedimiento|ejecut\w*|parche)\b|\b(update|insert|delete)\s",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Terms in failure notes indicating the planner/diagnostic loop itself failed.</summary>
    private static readonly Regex PlannerFailurePattern = new(
        @"planner|diagn[oó]stic|hip[oó]tesis|root\s*cause",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex KeywordPattern = new(
        @"[\p{L}\p{N}_]+",
        RegexOptions.Compiled);

    private static readonly Regex SlugInvalidCharsPattern = new(
        @"[^a-z0-9]+",
        RegexOptions.Compiled);

    private static readonly Regex CodeInvalidCharsPattern = new(
        @"[^a-z0-9_-]+",
        RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly KernelFactory _kernelFactory;
    private readonly ITenantConfigService _tenantConfigService;
    private readonly IOptions<HiveOpsDeploymentOptions> _deploymentOptions;
    private readonly ILogger<TenantImprovementService> _logger;
    private readonly Func<Guid, string, string, CancellationToken, Task<string>> _chatCompletion;
    private readonly ILlmTaskQueue? _taskQueue;

    public TenantImprovementService(
        AppDbContext db,
        KernelFactory kernelFactory,
        ITenantConfigService tenantConfigService,
        IOptions<HiveOpsDeploymentOptions> deploymentOptions,
        ILogger<TenantImprovementService> logger,
        ILlmTaskQueue? taskQueue = null)
        : this(db, kernelFactory, tenantConfigService, deploymentOptions, logger,
            chatCompletion: null, taskQueue)
    {
    }

    /// <summary>
    /// Test seam: <paramref name="chatCompletion"/> replaces the Semantic Kernel
    /// round-trip so draft generation can be unit-tested deterministically.
    /// </summary>
    internal TenantImprovementService(
        AppDbContext db,
        KernelFactory kernelFactory,
        ITenantConfigService tenantConfigService,
        IOptions<HiveOpsDeploymentOptions> deploymentOptions,
        ILogger<TenantImprovementService> logger,
        Func<Guid, string, string, CancellationToken, Task<string>>? chatCompletion,
        ILlmTaskQueue? taskQueue = null)
    {
        _db = db;
        _kernelFactory = kernelFactory;
        _tenantConfigService = tenantConfigService;
        _deploymentOptions = deploymentOptions;
        _logger = logger;
        _chatCompletion = chatCompletion ?? CompleteChatAsync;
        _taskQueue = taskQueue;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantImprovementSuggestion>> ListAsync(
        Guid tenantId, string? status = null, CancellationToken ct = default)
    {
        var query = _db.TenantImprovementSuggestions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(s => s.Status == status);
        else
            query = query.Where(s => s.Status == "Suggested" || s.Status == "Converted");

        return await query
            .OrderByDescending(s => s.CreatedAtUtc)
            .Take(MaxListResults)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<int> GenerateFromFailuresAsync(
        Guid tenantId, int takeLast = 50, CancellationToken ct = default)
    {
        var take = Math.Clamp(takeLast, 1, MaxListResults);

        // Failed = still-open incidents escalated to a human engineer. Same detection
        // rule as TenantContextPackService.RebuildResolutionMemoryAsync.
        var escalated = await _db.Incidents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId
                && i.AssignedTo == "engineer"
                && i.Status != IncidentStatus.Resolved
                && i.Status != IncidentStatus.Closed
                && i.ResolvedAt == null)
            .OrderByDescending(i => i.UpdatedAt)
            .Take(take)
            .ToListAsync(ct);

        // Also surface incidents whose resolution-memory entry in the context pack is
        // marked failed (succeeded=false). Memory entries carry no IncidentId, so they
        // are matched back by their recorded symptom (the incident title at write time);
        // IncidentId dedupe downstream makes fuzzy collisions harmless for v1.
        var pack = await _db.TenantContextPacks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);

        var failedSymptoms = TryParseJsonArray(pack?.ResolutionMemoryJson)?
            .Where(e => TryGetBool(e, "succeeded") == false)
            .Select(e => e?["symptom"]?.GetValue<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct()
            .ToList() ?? [];

        var failedFromMemory = failedSymptoms.Count == 0
            ? new List<Incident>()
            : await _db.Incidents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(i => i.TenantId == tenantId && failedSymptoms.Contains(i.Title))
                .OrderByDescending(i => i.UpdatedAt)
                .Take(take)
                .ToListAsync(ct);

        var incidents = escalated
            .Concat(failedFromMemory)
            .GroupBy(i => i.Id)
            .Select(g => g.First())
            .Take(take)
            .ToList();

        var created = 0;
        foreach (var incident in incidents)
        {
            try
            {
                var (_, wasCreated) = await EnsureCoreAsync(tenantId, incident, notes: null, ct);
                if (wasCreated)
                    created++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad incident must not kill the scan.
                _logger.LogWarning(ex,
                    "Improvement scan skipped incident {IncidentId} for tenant {TenantId}.",
                    incident.Id, tenantId);
            }
        }

        return created;
    }

    /// <inheritdoc />
    public async Task<TenantImprovementSuggestion?> EnsureForFailedIncidentAsync(
        Guid tenantId, Incident incident, string? notes, CancellationToken ct = default)
    {
        try
        {
            return (await EnsureCoreAsync(tenantId, incident, notes, ct)).Suggestion;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hot-path safety: auto-ticketing must never break the caller.
            _logger.LogWarning(ex,
                "Failed to ensure improvement suggestion for tenant {TenantId} (incident {IncidentId}).",
                tenantId, incident.Id);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<TenantImprovementSuggestion?> GenerateDraftAsync(
        Guid tenantId, Guid suggestionId, CancellationToken ct = default)
    {
        var suggestion = await _db.TenantImprovementSuggestions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == suggestionId, ct);

        if (suggestion is null
            || !string.Equals(suggestion.Status, "Suggested", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var systemPrompt = """
                You are the HiveOps improvement drafter. Given a self-improvement suggestion
                (kind, description, evidence, current draft skeleton), complete the artifact
                payload as JSON.

                For kind "NewPlaybook" or "DataQualityRule" respond with:
                { "code": "fix_snake_case", "name": "short name",
                  "triggerSymptoms": "symptoms/keywords used to match future incidents",
                  "actionType": "SqlTemplate",
                  "actionPayloadTemplate": "parameterized SQL using @param placeholders",
                  "requiredParameters": ["param"],
                  "verificationSql": "read-only SELECT returning 0 rows when healthy" }

                For kind "KbGap" or "PlannerGap" respond with:
                { "title": "...", "body": "markdown body", "tags": ["..."] }

                RULES:
                - actionPayloadTemplate must bind every runtime value via @param placeholders;
                  never interpolate values into SQL text. Prefer UPDATE/SELECT; no DDL, no DROP.
                - verificationSql must be a read-only SELECT returning 0 rows when the system is healthy.
                - Every placeholder used in actionPayloadTemplate must appear in requiredParameters.
                - Respond ONLY with valid JSON, no markdown fences, no commentary.
                """;

            var userMessage =
                $"SUGGESTION\nKind: {suggestion.Kind}\nTitle: {suggestion.Title}\n" +
                $"Description: {suggestion.Description}\n\n" +
                $"EVIDENCE:\n{suggestion.EvidenceJson}\n\n" +
                $"CURRENT DRAFT SKELETON:\n{suggestion.DraftPayloadJson}";

            var raw = await _chatCompletion(tenantId, systemPrompt, userMessage, ct);
            var parsed = TryParseJsonObject(raw);
            if (parsed is not null)
            {
                suggestion.DraftPayloadJson = parsed.ToJsonString();
            }
            else
            {
                _logger.LogWarning(
                    "LLM returned an unparseable draft for suggestion {SuggestionId} (tenant {TenantId}); keeping existing draft.",
                    suggestionId, tenantId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "LLM draft generation failed for suggestion {SuggestionId} (tenant {TenantId}); keeping existing draft.",
                suggestionId, tenantId);

            if (_taskQueue is not null)
                await _taskQueue.EnqueueAsync(
                    tenantId,
                    LlmPendingTask.TaskImprovementDraft,
                    JsonSerializer.Serialize(new { suggestionId }),
                    $"improvement-draft:{suggestionId}",
                    ex.Message,
                    ct);
        }

        suggestion.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return suggestion;
    }

    /// <inheritdoc />
    public async Task<TenantPlaybook?> ConvertToPlaybookAsync(
        Guid tenantId, Guid suggestionId, string? decidedBy, CancellationToken ct = default)
    {
        var suggestion = await _db.TenantImprovementSuggestions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == suggestionId, ct);

        if (suggestion is null
            || !string.Equals(suggestion.Status, "Suggested", StringComparison.OrdinalIgnoreCase)
            || !PlaybookKinds.Contains(suggestion.Kind))
            return null;

        var draft = TryParseJsonObject(suggestion.DraftPayloadJson);
        var template = ReadDraftString(draft, "actionPayloadTemplate");
        if (string.IsNullOrWhiteSpace(template))
        {
            // No executable action yet — the UI tells the reviewer to generate a draft first.
            return null;
        }

        var actionType = ReadDraftString(draft, "actionType");
        if (string.IsNullOrWhiteSpace(actionType) || !AllowedActionTypes.Contains(actionType))
            actionType = "SqlTemplate";

        var code = await ResolveUniquePlaybookCodeAsync(
            tenantId,
            SanitizePlaybookCode(ReadDraftString(draft, "code") ?? suggestion.Title),
            ct);

        var playbook = new TenantPlaybook
        {
            TenantId = tenantId,
            Code = code,
            Name = Truncate(ReadDraftString(draft, "name") ?? suggestion.Title, MaxTitleChars),
            TriggerSymptoms = ReadDraftString(draft, "triggerSymptoms") ?? string.Empty,
            ActionType = actionType,
            ActionPayloadTemplate = template,
            RequiredParametersJson = SerializeStringArray(draft?["requiredParameters"]),
            VerificationSqlQuery = ReadDraftString(draft, "verificationSql"),
            // Auto-drafted: approval-gated, disabled and tightly capped until a human reviews it.
            RequiresApproval = true,
            IsActive = false,
            MaxAffectedRows = AutoDraftMaxAffectedRows
        };
        _db.TenantPlaybooks.Add(playbook);

        suggestion.Status = "Converted";
        suggestion.ConvertedPlaybookId = playbook.Id;
        suggestion.DecidedBy = decidedBy;
        suggestion.DecidedAtUtc = DateTimeOffset.UtcNow;
        suggestion.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return playbook;
    }

    /// <inheritdoc />
    public async Task<TenantImprovementSuggestion?> SetStatusAsync(
        Guid tenantId, Guid suggestionId, string status, string? decidedBy,
        CancellationToken ct = default)
    {
        var canonical = DecidedStatuses.FirstOrDefault(
            s => string.Equals(s, status, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
            return null;

        var suggestion = await _db.TenantImprovementSuggestions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == suggestionId, ct);
        if (suggestion is null)
            return null;

        suggestion.Status = canonical;
        suggestion.DecidedBy = decidedBy;
        suggestion.DecidedAtUtc = DateTimeOffset.UtcNow;
        suggestion.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return suggestion;
    }

    // ─── Suggestion creation ────────────────────────────────────────────────

    /// <summary>
    /// Core of <see cref="EnsureForFailedIncidentAsync"/>: dedupe per IncidentId —
    /// existing suggestion → bump OccurrenceCount; otherwise create a new Suggested
    /// entry with classified Kind, evidence and a draft skeleton. Returns the
    /// suggestion plus whether it was newly created. May throw; callers wrap.
    /// </summary>
    private async Task<(TenantImprovementSuggestion? Suggestion, bool Created)> EnsureCoreAsync(
        Guid tenantId, Incident incident, string? notes, CancellationToken ct)
    {
        var existing = await _db.TenantImprovementSuggestions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                s => s.TenantId == tenantId && s.IncidentId == incident.Id, ct);

        if (existing is not null)
        {
            existing.OccurrenceCount++;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return (existing, false);
        }

        var kind = ClassifyKind(incident, notes);
        var suggestion = new TenantImprovementSuggestion
        {
            TenantId = tenantId,
            IncidentId = incident.Id,
            Kind = kind,
            Title = Truncate($"Mejora: {incident.Title}", MaxTitleChars),
            Description = BuildDescription(incident, notes),
            EvidenceJson = BuildEvidence(incident, notes).ToJsonString(),
            DraftPayloadJson = BuildDraftPayload(incident, kind).ToJsonString(),
            Status = "Suggested",
            OccurrenceCount = 1,
            CreatedBy = "auto",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        _db.TenantImprovementSuggestions.Add(suggestion);
        await _db.SaveChangesAsync(ct);
        return (suggestion, true);
    }

    /// <summary>
    /// Kind classification heuristic, evaluated in order:
    /// 1. Data-dirty symptom (category/title/description mentions stock, precio, fecha,
    ///    nulo, duplicado, faltante, artículo, remito, proveedor, tabla) → "DataQualityRule".
    /// 2. A deterministic fix (SQL/script/playbook/stored proc) was attempted or the
    ///    incident carries a verification query → "NewPlaybook".
    /// 3. No root cause or diagnostic note was produced → "KbGap" (missing knowledge).
    /// 4. Notes mention planner/diagnostic/hypothesis/root-cause failure → "PlannerGap".
    /// 5. Fallback → "NewPlaybook".
    /// </summary>
    private static string ClassifyKind(Incident incident, string? notes)
    {
        var incidentText = $"{incident.Category} {incident.Title} {incident.Description}";
        var notesText = notes
            ?? incident.ResolutionNotes
            ?? incident.TechnicalDiagnosticNote
            ?? string.Empty;

        if (DataishTermPattern.IsMatch(incidentText))
            return "DataQualityRule";

        if (FixAttemptPattern.IsMatch(notesText)
            || !string.IsNullOrWhiteSpace(incident.VerificationQuery))
            return "NewPlaybook";

        if (string.IsNullOrWhiteSpace(incident.RootCauseJson)
            && string.IsNullOrWhiteSpace(incident.TechnicalDiagnosticNote))
            return "KbGap";

        if (PlannerFailurePattern.IsMatch(notesText))
            return "PlannerGap";

        return "NewPlaybook";
    }

    private static string BuildDescription(Incident incident, string? notes)
    {
        var failure = FirstNonBlank(notes, incident.ResolutionNotes, incident.TechnicalDiagnosticNote)
            ?? "escalated without an automated resolution";

        return Truncate(
            $"Incident '{incident.Title}' could not be auto-resolved and was escalated to " +
            $"'{incident.AssignedTo}'. Symptom: {incident.Description} " +
            $"Failure detail: {failure}",
            MaxDescriptionChars);
    }

    private static JsonObject BuildEvidence(Incident incident, string? notes)
    {
        var failedAttempts = new JsonArray();
        foreach (var attempt in new[] { notes, incident.ResolutionNotes, incident.TechnicalDiagnosticNote })
        {
            if (!string.IsNullOrWhiteSpace(attempt))
                failedAttempts.Add(Truncate(attempt, 400));
        }

        var relatedTerms = new JsonArray();
        foreach (var term in ExtractKeywords($"{incident.Title} {incident.Description}"))
            relatedTerms.Add(term);

        return new JsonObject
        {
            ["symptoms"] = Truncate(incident.Description, MaxSymptomChars),
            ["failedAttempts"] = failedAttempts,
            ["relatedTerms"] = relatedTerms,
            ["category"] = incident.Category.ToString(),
            ["escalatedTo"] = incident.AssignedTo
        };
    }

    /// <summary>
    /// Heuristic draft skeleton. Playbook-like kinds carry the TenantPlaybook field
    /// shape with an empty actionPayloadTemplate (filled later by GenerateDraftAsync);
    /// knowledge/planner kinds carry a KB-article shape.
    /// </summary>
    private static JsonObject BuildDraftPayload(Incident incident, string kind)
    {
        if (PlaybookKinds.Contains(kind))
        {
            return new JsonObject
            {
                ["code"] = $"fix_{Slugify(incident.Title)}",
                ["name"] = Truncate(incident.Title, MaxTitleChars),
                ["triggerSymptoms"] = Truncate(incident.Description, MaxTriggerSymptomChars),
                ["actionType"] = "SqlTemplate",
                ["actionPayloadTemplate"] = string.Empty,
                ["requiredParameters"] = new JsonArray(),
                ["verificationSql"] = string.Empty
            };
        }

        return new JsonObject
        {
            ["title"] = incident.Title,
            ["body"] = string.Empty,
            ["tags"] = new JsonArray()
        };
    }

    // ─── Playbook conversion helpers ────────────────────────────────────────

    private async Task<string> ResolveUniquePlaybookCodeAsync(
        Guid tenantId, string baseCode, CancellationToken ct)
    {
        var existingCodes = await _db.TenantPlaybooks
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId)
            .Select(p => p.Code)
            .ToListAsync(ct);
        var taken = new HashSet<string>(existingCodes, StringComparer.OrdinalIgnoreCase);

        var code = baseCode;
        for (var suffix = 2; taken.Contains(code); suffix++)
            code = $"{baseCode}-{suffix}";

        return code;
    }

    /// <summary>Normalizes a draft code to [a-z0-9_-]; falls back to "fix_incident".</summary>
    private static string SanitizePlaybookCode(string? raw)
    {
        var code = CodeInvalidCharsPattern
            .Replace((raw ?? string.Empty).ToLowerInvariant(), "-")
            .Trim('-', '_');
        return string.IsNullOrEmpty(code) ? "fix_incident" : code;
    }

    /// <summary>Serializes a JsonArray-of-strings node into RequiredParametersJson; "[]" when absent/invalid.</summary>
    private static string SerializeStringArray(JsonNode? node)
    {
        if (node is JsonArray arr)
        {
            var names = arr
                .Select(e => e is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            return JsonSerializer.Serialize(names);
        }

        return "[]";
    }

    /// <summary>
    /// Reads a scalar string from an LLM-produced draft field. LLMs occasionally return
    /// arrays where a string is expected (e.g. triggerSymptoms) — arrays are joined with
    /// ", " instead of throwing on GetValue&lt;string&gt;.
    /// </summary>
    private static string? ReadDraftString(JsonObject? draft, string key)
    {
        var node = draft?[key];
        return node switch
        {
            null => null,
            JsonArray arr => string.Join(", ", arr
                .Select(e => e is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))),
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => node.ToJsonString()
        };
    }

    // ─── Text helpers ───────────────────────────────────────────────────────

    private static IReadOnlyList<string> ExtractKeywords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return KeywordPattern.Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(t => t.Length > 4)
            .Distinct()
            .Take(MaxKeywords)
            .ToList();
    }

    private static string Slugify(string? text)
    {
        var slug = SlugInvalidCharsPattern
            .Replace((text ?? string.Empty).ToLowerInvariant(), "_")
            .Trim('_');
        if (slug.Length > MaxSlugChars)
            slug = slug[..MaxSlugChars].TrimEnd('_');
        return string.IsNullOrEmpty(slug) ? "incident" : slug;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    // ─── Tolerant JSON helpers ──────────────────────────────────────────────

    /// <summary>
    /// Parses a JSON object leniently: strips markdown fences and extracts the
    /// outermost {…} span. Returns null on any parse failure.
    /// </summary>
    private static JsonObject? TryParseJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = StripMarkdownJsonFence(raw.Trim());
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        try
        {
            return JsonNode.Parse(text[start..(end + 1)]) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonArray? TryParseJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool? TryGetBool(JsonNode? node, string property)
    {
        try
        {
            return node?[property]?.GetValue<bool>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string StripMarkdownJsonFence(string raw)
    {
        if (!raw.StartsWith("```", StringComparison.Ordinal))
            return raw;

        var firstNl = raw.IndexOf('\n');
        if (firstNl < 0)
            return raw;

        var body = raw[(firstNl + 1)..];
        if (body.EndsWith("```", StringComparison.Ordinal))
            body = body[..^3].Trim();

        return body;
    }

    // ─── LLM plumbing ───────────────────────────────────────────────────────

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
}
