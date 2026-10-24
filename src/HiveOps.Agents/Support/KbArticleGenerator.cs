using System.Collections.Generic;
using System.Text.Json;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Services;
using HiveOps.Application.Support;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace HiveOps.Agents.Support;

/// <summary>
/// Generates KbArticle from resolved incidents with conversation history using LLM.
/// </summary>
public sealed class KbArticleGenerator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IIncidentMessageHistoryService _messageHistory;
    private readonly KernelFactory _kernelFactory;
    private readonly ILogger<KbArticleGenerator> _logger;

    public KbArticleGenerator(
        IServiceScopeFactory scopeFactory,
        IIncidentMessageHistoryService messageHistory,
        KernelFactory kernelFactory,
        ILogger<KbArticleGenerator> logger)
    {
        _scopeFactory = scopeFactory;
        _messageHistory = messageHistory;
        _kernelFactory = kernelFactory;
        _logger = logger;
    }

    /// <summary>
    /// Pre-saves a draft KbArticle while the incident is parked at
    /// AwaitingApproval: the diagnosis + proposed fix are captured immediately,
    /// so the knowledge is not lost if the incident stalls. The article stays
    /// unpublished (never served by KB search) until the incident resolves —
    /// at that point <see cref="GenerateFromIncidentAsync"/> refreshes the same
    /// row with the real resolution instead of duplicating it.
    /// </summary>
    public async Task<KbArticle?> GenerateDraftFromIncidentAsync(
        Guid incidentId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var incident = await LoadIncidentAsync(db, incidentId, cancellationToken);
        if (incident is null)
            return null;

        // Drafts only make sense while the incident carries a diagnosis or a
        // pending proposal; resolved/closed incidents go through the final path.
        if (incident.Status is IncidentStatus.Resolved or IncidentStatus.Closed)
            return null;
        if (incident.Status is not IncidentStatus.AwaitingApproval)
            return null;
        if (string.IsNullOrWhiteSpace(incident.RootCauseJson)
            && string.IsNullOrWhiteSpace(incident.ResolutionNotes))
            return null;

        var article = await db.KbArticles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.SourceIncidentId == incidentId, cancellationToken);
        if (article is null)
        {
            article = new KbArticle
            {
                Id = Guid.NewGuid(),
                TenantId = incident.TenantId,
                SourceIncidentId = incident.Id,
                IsPublished = false, // draft: never served by KB search until review
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.KbArticles.Add(article);
        }

        article.Title = GenerateDraftTitle(incident);
        article.Content = GenerateDraftContent(incident);
        article.Category = incident.Category.ToString();
        article.Tags = GenerateDraftTags(incident);
        article.ResolutionSteps = await GenerateResolutionSteps(incident, "Propuesta pendiente de aprobación.", cancellationToken);
        article.UpdatedAt = DateTimeOffset.UtcNow;

        await RefreshEmbeddingAsync(scope.ServiceProvider, article, incidentId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return article;
    }

    /// <summary>
    /// Generates a KbArticle from a resolved incident.
    /// </summary>
    public async Task<KbArticle> GenerateFromIncidentAsync(
        Guid incidentId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Internal post-resolution path: the scope has no tenant context, so the
        // global query filter would hide the incident. IgnoreQueryFilters is safe
        // here because the incident is addressed by primary key.
        var incident = await LoadIncidentAsync(db, incidentId, cancellationToken);

        if (incident is null)
            throw new InvalidOperationException($"Incident {incidentId} not found");

        // Eager generation runs at Resolved (before user confirmation); allow both.
        if (incident.Status is not (IncidentStatus.Resolved or IncidentStatus.Closed))
            throw new InvalidOperationException($"Incident {incidentId} is not resolved");

        var history = await _messageHistory.GetFormattedHistoryAsync(incidentId, cancellationToken);

        // Upsert: a draft may already exist (pre-saved at AwaitingApproval) —
        // refresh it with the real resolution instead of inserting a duplicate.
        var article = await db.KbArticles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.SourceIncidentId == incidentId, cancellationToken);
        if (article is null)
        {
            article = new KbArticle
            {
                Id = Guid.NewGuid(),
                TenantId = incident.TenantId,
                SourceIncidentId = incident.Id,
                IsPublished = false, // Requires manual review
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.KbArticles.Add(article);
        }

        article.Title = GenerateTitle(incident);
        article.Content = GenerateContent(incident, history);
        article.Category = incident.Category.ToString();
        article.Tags = GenerateTags(incident);
        article.ResolutionSteps = await GenerateResolutionSteps(incident, history, cancellationToken);
        article.UpdatedAt = DateTimeOffset.UtcNow;

        // Best-effort embedding so KB search (IncidentAnalysisEngine / SupportPlugin.SearchKbAsync)
        // can rank by cosine similarity. IEmbeddingService is scoped while this generator is
        // registered as a singleton, so it is resolved from the per-call scope.
        await RefreshEmbeddingAsync(scope.ServiceProvider, article, incidentId, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return article;
    }

    private static Task<Incident?> LoadIncidentAsync(AppDbContext db, Guid incidentId, CancellationToken cancellationToken)
    {
        return db.Incidents
            .IgnoreQueryFilters()
            .Include(i => i.Messages)
            .FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);
    }

    private async Task RefreshEmbeddingAsync(
        IServiceProvider services,
        KbArticle article,
        Guid incidentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var embeddingService = services.GetService<IEmbeddingService>();
            if (embeddingService is not null)
            {
                var vector = await embeddingService.GenerateEmbeddingAsync(
                    $"{article.Title}\n{article.Content}\n{article.ResolutionSteps}",
                    cancellationToken);
                if (vector is { Length: > 0 })
                    article.EmbeddingVector = EmbeddingMath.FloatsToLittleEndianBytes(vector);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Embedding generation failed for KB article from incident {IncidentId}; saving without vector.",
                incidentId);
        }
    }

    private string GenerateDraftTitle(Incident incident)
    {
        var title = incident.Title.Length > 100
            ? incident.Title.Substring(0, 97) + "..."
            : incident.Title;

        return $"Borrador: {title}";
    }

    private string GenerateDraftContent(Incident incident)
    {
        string hypothesis = "(sin hipótesis registrada)";
        string? verification = null;
        if (!string.IsNullOrWhiteSpace(incident.RootCauseJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(incident.RootCauseJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("SummaryHypothesis", out var h) && h.ValueKind == JsonValueKind.String)
                    hypothesis = h.GetString() ?? hypothesis;
                if (root.TryGetProperty("VerificationSqlQuery", out var v) && v.ValueKind == JsonValueKind.String)
                    verification = v.GetString();
            }
            catch (JsonException)
            {
                // keep defaults
            }
        }

        return $"""
            ## Problema
            {incident.Description}

            ## Severidad
            {incident.Severity}

            ## Categoría
            {incident.Category}

            ## Hipótesis de causa raíz (pendiente de confirmación)
            {hypothesis}

            ## Propuesta de corrección (pendiente de aprobación)
            {incident.ResolutionNotes ?? "Sin propuesta registrada."}

            ## Query de verificación sugerida
            {verification ?? "No registrada."}

            > Borrador generado automáticamente mientras el incidente espera aprobación.
            > Al resolverse el incidente, este artículo se actualiza con la resolución real.
            """;
    }

    private string GenerateDraftTags(Incident incident)
    {
        var tags = new List<string>
        {
            incident.Category.ToString(),
            incident.Severity.ToString(),
            "borrador",
            "pendiente-resolución",
            "bot-generado"
        };

        return string.Join(", ", tags);
    }

    private string GenerateTitle(Incident incident)
    {
        // Generate a concise title from the incident
        var title = incident.Title.Length > 100
            ? incident.Title.Substring(0, 97) + "..."
            : incident.Title;

        return $"Resolución: {title}";
    }

    private string GenerateContent(Incident incident, string history)
    {
        // Generate content from incident description and conversation history
        return $"""
            ## Problema
            {incident.Description}

            ## Severidad
            {incident.Severity}

            ## Categoría
            {incident.Category}

            ## Historial de la conversación
            {history}

            ## Notas de resolución
            {incident.ResolutionNotes ?? "No se proporcionaron notas de resolución."}
            """;
    }

    private string GenerateTags(Incident incident)
    {
        // Generate tags from incident metadata
        var tags = new List<string>
        {
            incident.Category.ToString(),
            incident.Severity.ToString(),
            "resuelto",
            "bot-generado"
        };

        return string.Join(", ", tags);
    }

    private async Task<string> GenerateResolutionSteps(Incident incident, string history, CancellationToken cancellationToken)
    {
        // Use LLM to extract and format resolution steps from conversation history
        try
        {
            using var cfgScope = _scopeFactory.CreateScope();
            var tenantConfigs = cfgScope.ServiceProvider.GetRequiredService<ITenantConfigService>();
            var deploymentOpts = cfgScope.ServiceProvider.GetRequiredService<IOptions<HiveOpsDeploymentOptions>>();
            var exec = await TenantLlmExecutionHelper.GetChatExecutionSettingsAsync(
                incident.TenantId, tenantConfigs, deploymentOpts, cancellationToken);

            var kernel = _kernelFactory.CreateForTenant(incident.TenantId);
            var prompt = $"""
                Based on the following incident resolution conversation, extract the specific resolution steps taken.

                Incident: {incident.Title}
                Description: {incident.Description}
                Category: {incident.Category}
                Resolution Notes: {incident.ResolutionNotes}

                Conversation History:
                {history}

                Please extract the actual resolution steps taken (not generic steps). Format as a numbered list.
                If no specific steps are documented, state that explicitly.
                """;

            var args = new KernelArguments
            {
                ExecutionSettings = new Dictionary<string, PromptExecutionSettings>(StringComparer.OrdinalIgnoreCase)
                {
                    [PromptExecutionSettings.DefaultServiceId] = exec
                }
            };
            var result = await kernel.InvokePromptAsync(prompt, args, cancellationToken: cancellationToken);
            var steps = result.ToString();

            if (string.IsNullOrWhiteSpace(steps) || steps.Contains("no specific steps", StringComparison.OrdinalIgnoreCase))
            {
                // Fallback to generic steps if LLM couldn't extract specific ones
                return GenerateGenericSteps(incident);
            }

            return steps;
        }
        catch (Exception)
        {
            // Fallback to generic steps if LLM fails
            return GenerateGenericSteps(incident);
        }
    }

    private string GenerateGenericSteps(Incident incident)
    {
        var steps = new List<string>
        {
            "1. Analizar el incidente y clasificar su categoría y severidad.",
            "2. Ejecutar diagnósticos según la categoría (código o base de datos).",
            "3. Proponer solución basada en el diagnóstico.",
            $"4. Si la solución afecta a más de 3 registros o líneas de código, solicitar aprobación humana.",
            "5. Ejecutar la solución aprobada.",
            "6. Verificar que el problema está resuelto.",
            "7. Documentar la resolución y cerrar el incidente."
        };

        return string.Join("\n", steps);
    }
}
