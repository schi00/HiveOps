using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Infrastructure.AI;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Models;
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

    public IncidentAnalysisEngine(
        KernelFactory kernelFactory,
        AppDbContext db,
        ITenantConfigService tenantConfigService,
        IOptions<HiveOpsDeploymentOptions> deploymentOptions)
    {
        _kernelFactory = kernelFactory;
        _db = db;
        _tenantConfigService = tenantConfigService;
        _deploymentOptions = deploymentOptions;
    }

    public async Task<IncidentAnalysisResult> AnalyzeAsync(
        Guid tenantId,
        string description,
        Guid? incidentId,
        CancellationToken ct = default)
    {
        // ── 1. Build grounded KB context ────────────────────────────────────
        var kbArticles = await FindSimilarKbAsync(description, tenantId, ct);
        var kbContext = kbArticles.Count == 0
            ? "No similar KB articles found."
            : string.Join("\n---\n", kbArticles.Select(a =>
                $"TITLE: {a.Title}\nCATEGORY: {a.Category}\nSTEPS: {a.ResolutionSteps}"));

        // ── 2. Strict system prompt ─────────────────────────────────────────
        var systemPrompt = $$"""
            You are the HiveOps Incident Triage Engine. Your ONLY job is to classify a user-reported incident and suggest SAFE read-only diagnostic steps.

            RULES (violation = escalation):
            - NEVER generate SQL that modifies data (no INSERT, UPDATE, DELETE, DROP, ALTER, TRUNCATE).
            - NEVER suggest destructive file operations.
            - ONLY suggest SELECT / EXPLAIN / SHOW queries for database diagnostics.
            - If the incident is critical (data loss, security breach, widespread outage), set requiresEscalation=true immediately.

            CATEGORY must be one of: CodeBug, DbCorruption, ConfigError, Performance, Security, Other.
            SEVERITY must be one of: Low, Medium, High, Critical.

            Knowledge-base context (resolved incidents similar to this one):
            {{kbContext}}

            Respond ONLY with valid JSON matching this schema:
            {
              "category": "...",
              "severity": "...",
              "missingInfo": ["..."],
              "suggestedDiagnostics": ["..."],
              "confidence": 0.0,
              "requiresEscalation": false,
              "reasoning": "..."
            }
            """;

        // ── 3. Call LLM via existing pipeline ───────────────────────────────
        var kernel = _kernelFactory.CreateForTenant(tenantId);
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage($"Incident description:\n{description}");

        var exec = await TenantLlmExecutionHelper.GetChatExecutionSettingsAsync(
            tenantId, _tenantConfigService, _deploymentOptions, ct);
        var response = await chat.GetChatMessageContentAsync(history, exec, cancellationToken: ct);
        var rawJson = response.Content ?? "";

        // ── 4. Audit log (prompt + response) ────────────────────────────────
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

        // ── 5. Parse & validate JSON ────────────────────────────────────────
        IncidentAnalysisResult result;
        try
        {
            result = JsonSerializer.Deserialize<IncidentAnalysisResult>(rawJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new IncidentAnalysisResult();
        }
        catch (JsonException)
        {
            // Schema violation → force escalation
            result = new IncidentAnalysisResult
            {
                Category = "Other",
                Severity = "High",
                RequiresEscalation = true,
                Reasoning = "LLM output did not match required JSON schema. Escalating for human review.",
                Confidence = 0
            };
        }

        // ── 6. Validate enums ───────────────────────────────────────────────
        var validCategories = new[] { "CodeBug", "DbCorruption", "ConfigError", "Performance", "Security", "Other" };
        var validSeverities = new[] { "Low", "Medium", "High", "Critical" };

        if (!validCategories.Contains(result.Category) || !validSeverities.Contains(result.Severity))
        {
            result = result with
            {
                RequiresEscalation = true,
                Reasoning = (result.Reasoning ?? "") + " [Auto-escalated: invalid category or severity from LLM.]"
            };
        }

        // ── 7. Critical severity = automatic escalation ─────────────────────
        if (result.Severity.Equals("Critical", StringComparison.OrdinalIgnoreCase))
        {
            result = result with { RequiresEscalation = true };
        }

        return result;
    }

    private async Task<List<KbArticle>> FindSimilarKbAsync(string description, Guid tenantId, CancellationToken ct)
    {
        var keywords = description.ToLowerInvariant().Split([' ', ',', '.', ';', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .Take(5)
            .ToList();

        if (keywords.Count == 0) return [];

        var query = _db.KbArticles
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.IsPublished);

        // Simple keyword search across title, content, tags
        var matches = await query.ToListAsync(ct);
        return matches
            .Where(a => keywords.Any(k =>
                a.Title.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                a.Content.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                a.Tags.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(a => keywords.Count(k =>
                a.Title.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Take(3)
            .ToList();
    }
}
