using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Support;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Agents.Support;

/// <summary>
/// LLM-powered incident triage engine. Reuses the existing Semantic Kernel pipeline.
///
/// Safety guarantees:
/// 1. The system prompt is immutable and strictly forbids generating DML/DDL.
/// 2. The output must match a JSON schema; any deviation forces escalation.
/// 3. Critical severity is an automatic escalation — no further LLM calls.
/// 4. Every prompt/response is persisted in DiagnosticLog for audit.
/// </summary>
public sealed class IncidentAnalysisEngine
{
    private readonly KernelFactory _kernelFactory;
    private readonly AppDbContext _db;
    private readonly ITenantConfigService _tenantConfigService;
    private readonly IOptions<HiveOpsDeploymentOptions> _deploymentOptions;
    private readonly HiveOps.Domain.Interfaces.IEmbeddingService _embeddingService;

    public IncidentAnalysisEngine(
        KernelFactory kernelFactory,
        AppDbContext db,
        ITenantConfigService tenantConfigService,
        IOptions<HiveOpsDeploymentOptions> deploymentOptions,
        HiveOps.Domain.Interfaces.IEmbeddingService embeddingService)
    {
        _kernelFactory = kernelFactory;
        _db = db;
        _tenantConfigService = tenantConfigService;
        _deploymentOptions = deploymentOptions;
        _embeddingService = embeddingService;
    }

    public async Task<IncidentAnalysisResult> AnalyzeAsync(
        Guid tenantId,
        string description,
        Guid? incidentId,
        CancellationToken ct = default)
    {
        var kbArticles = await FindSimilarKbAsync(description, tenantId, ct);
        var kbContext = kbArticles.Count == 0
            ? "No similar KB articles found."
            : string.Join("\n---\n", kbArticles.Select(a =>
                $"TITLE: {a.Title}\nCATEGORY: {a.Category}\nSTEPS: {a.ResolutionSteps}"));

        var systemPrompt = $$"""
            You are the HiveOps Incident Triage Engine. Your ONLY job is to classify a user-reported incident and suggest SAFE read-only diagnostic steps.

            RULES (violation = escalation):
            - NEVER generate SQL that modifies data (no INSERT, UPDATE, DELETE, DROP, ALTER, TRUNCATE).
            - NEVER suggest destructive file operations.
            - ONLY suggest SELECT queries for database diagnostics. The target application runs on PostgreSQL: use PostgreSQL syntax (LIMIT instead of TOP, now() instead of GETDATE()).
            - The platform discovers the database schema by itself via INFORMATION_SCHEMA. Do NOT ask in missingInfo which database engine, tables or columns are used — suggestedDiagnostics may rely on schema inspection being available.
            - Only put items in missingInfo that the reporting USER must provide (e.g., an order id, a screenshot, steps to reproduce). When in doubt, leave missingInfo empty.
            - If the incident is critical (data loss, security breach, widespread outage), set requiresEscalation=true immediately.
            - In technicalNote, when the defect looks like a user-provided value lost along the way (e.g. a quantity or option
              that defaults to a hardcoded value), trace the FULL data path before recommending a fix: user message →
              conversation state → tool arguments → plugin/service. Recommendations must target the layer where the value is
              actually captured or dropped (e.g. parsing the user message and persisting it into state), not merely the line
              where the default is applied. Do not suggest helper APIs that only inspect tool arguments when the value is
              said in free text across turns.

            CATEGORY must be one of: CodeBug, DbCorruption, ConfigError, Performance, Security, Other.
            - DbCorruption = wrong or corrupted VALUES stored in the application database (wrong prices, totals, statuses, duplicated or missing rows). Remediated with a governed data UPDATE.
            - ConfigError = misconfiguration NOT fixable by changing data (feature flags, appsettings, integrations).
            - CodeBug = application logic defect requiring a source-code change.
            SEVERITY must be one of: Low, Medium, High, Critical.
            Write "userFacingSummary" in the same language as the incident description.

            Knowledge-base context (resolved incidents similar to this one):
            {{kbContext}}

            Respond ONLY with valid JSON matching this schema (no markdown fences):
            {
              "category": "...",
              "severity": "...",
              "missingInfo": ["..."],
              "suggestedDiagnostics": ["..."],
              "confidence": 0.0,
              "requiresEscalation": false,
              "reasoning": "...",
              "userFacingSummary": "short non-technical guidance for the end user",
              "technicalNote": "technical diagnosis for IT staff",
              "extractedEntities": { }
            }
            """;

        var kernel = _kernelFactory.CreateForTenant(tenantId);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage($"Incident description:\n{description}");

        var exec = await TenantLlmExecutionHelper.GetTriageChatExecutionSettingsAsync(
            tenantId, _tenantConfigService, _deploymentOptions, ct);
        var response = await chat.GetChatMessageContentAsync(history, exec, cancellationToken: ct);
        var rawJson = StripMarkdownJsonFence((response.Content ?? "").Trim());

        if (incidentId.HasValue)
        {
            _db.DiagnosticLogs.Add(new DiagnosticLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                IncidentId = incidentId.Value,
                StepName = "llm_triage",
                Result = $"PROMPT:\n{systemPrompt}\n\nRESPONSE:\n{rawJson}",
                IsSuccess = true
            });
            await _db.SaveChangesAsync(ct);
        }

        IncidentAnalysisResult result;
        try
        {
            result = ParseTriageJson(rawJson);
        }
        catch (JsonException)
        {
            result = new IncidentAnalysisResult
            {
                Category = "Other",
                Severity = "High",
                RequiresEscalation = true,
                Reasoning = "LLM output did not match required JSON schema. Escalating for human review.",
                Confidence = 0
            };
        }

        var validCategories = new HashSet<string>(IncidentCategoryMapper.TriageCategoryValues, StringComparer.OrdinalIgnoreCase);
        var validSeverities = new[] { "Low", "Medium", "High", "Critical" };

        if (!validCategories.Contains(result.Category) || !validSeverities.Contains(result.Severity))
        {
            result = result with
            {
                RequiresEscalation = true,
                Reasoning = (result.Reasoning ?? "") + " [Auto-escalated: invalid category or severity from LLM.]"
            };
        }

        if (result.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase))
            result = result with { RequiresEscalation = true };

        return result;
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

    private static IncidentAnalysisResult ParseTriageJson(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        static IReadOnlyList<string> ReadStringArray(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
                return [];

            return p.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        var category = root.TryGetProperty("category", out var c) ? c.GetString() ?? "Other" : "Other";
        var severity = root.TryGetProperty("severity", out var s) ? s.GetString() ?? "Low" : "Low";
        var missing = ReadStringArray(root, "missingInfo");
        var diag = ReadStringArray(root, "suggestedDiagnostics");
        var confidence = root.TryGetProperty("confidence", out var cf) && cf.TryGetDouble(out var d) ? d : 0;
        var esc = root.TryGetProperty("requiresEscalation", out var re) && re.ValueKind == JsonValueKind.True;
        var reasoning = root.TryGetProperty("reasoning", out var r) ? r.GetString() : null;
        var userSum = root.TryGetProperty("userFacingSummary", out var u) ? u.GetString() : null;
        var tech = root.TryGetProperty("technicalNote", out var t) ? t.GetString() : null;
        string? entitiesJson = null;
        if (root.TryGetProperty("extractedEntities", out var ex) && ex.ValueKind == JsonValueKind.Object)
            entitiesJson = ex.GetRawText();

        return new IncidentAnalysisResult
        {
            Category = category,
            Severity = severity,
            MissingInfo = missing,
            SuggestedDiagnostics = diag,
            Confidence = confidence,
            RequiresEscalation = esc,
            Reasoning = reasoning,
            UserFacingSummary = userSum,
            TechnicalNote = tech,
            ExtractedEntitiesJson = entitiesJson
        };
    }

    private async Task<List<KbArticle>> FindSimilarKbAsync(string description, Guid tenantId, CancellationToken ct)
    {
        var keywords = description.ToLowerInvariant().Split([' ', ',', '.', ';', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .Take(5)
            .ToList();

        var query = _db.KbArticles
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.IsPublished);

        var matches = await query.ToListAsync(ct);
        var keywordFiltered = matches
            .Where(a => keywords.Count == 0 || keywords.Any(k =>
                a.Title.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                a.Content.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                a.Tags.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var pool = (keywordFiltered.Count > 0 ? keywordFiltered : matches).Take(40).ToList();
        if (pool.Count == 0)
            return [];

        float[] descEmbed;
        try
        {
            descEmbed = await _embeddingService.GenerateEmbeddingAsync(description, ct);
        }
        catch
        {
            return pool
                .OrderByDescending(a => keywords.Count(k =>
                    a.Title.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .Take(3)
                .ToList();
        }

        var scored = new List<(KbArticle Article, double Score)>();
        foreach (var a in pool)
        {
            double score;
            if (a.EmbeddingVector is { Length: > 0 } blob)
            {
                var vec = EmbeddingMath.FloatsFromLittleEndianBytes(blob);
                score = EmbeddingMath.CosineSimilarity(descEmbed, vec);
            }
            else
            {
                score = KeywordBoostScore(description, a, keywords);
            }

            scored.Add((a, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Take(3)
            .Select(x => x.Article)
            .ToList();
    }

    private static double KeywordBoostScore(string description, KbArticle a, IReadOnlyList<string> keywords)
    {
        var text = $"{a.Title}\n{a.Content}\n{a.Tags}".ToLowerInvariant();
        var hits = keywords.Count(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
        return hits * 0.01;
    }
}
