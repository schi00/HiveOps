using System.Collections.Generic;
using HiveOps.Application.Configuration;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Services;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    public KbArticleGenerator(
        IServiceScopeFactory scopeFactory,
        IIncidentMessageHistoryService messageHistory,
        KernelFactory kernelFactory)
    {
        _scopeFactory = scopeFactory;
        _messageHistory = messageHistory;
        _kernelFactory = kernelFactory;
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

        var incident = await db.Incidents
            .Include(i => i.Messages)
            .FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);

        if (incident is null)
            throw new InvalidOperationException($"Incident {incidentId} not found");

        if (incident.Status != IncidentStatus.Closed)
            throw new InvalidOperationException($"Incident {incidentId} is not resolved");

        var history = await _messageHistory.GetFormattedHistoryAsync(incidentId, cancellationToken);

        var article = new KbArticle
        {
            Id = Guid.NewGuid(),
            TenantId = incident.TenantId,
            SourceIncidentId = incident.Id,
            Title = GenerateTitle(incident),
            Content = GenerateContent(incident, history),
            Category = incident.Category.ToString(),
            Tags = GenerateTags(incident),
            ResolutionSteps = await GenerateResolutionSteps(incident, history, cancellationToken),
            IsPublished = false, // Requires manual review
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.KbArticles.Add(article);
        await db.SaveChangesAsync(cancellationToken);

        return article;
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
