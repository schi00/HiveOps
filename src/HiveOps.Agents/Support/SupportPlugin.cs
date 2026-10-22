using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using HiveOps.Application;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Support;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Agents.Support;

/// <summary>
/// SupportPlugin — analyzes incidents, proposes fixes, and manages resolution workflows.
/// Enforces safety rules through SafetyValidator before any destructive operation.
/// </summary>
public sealed class SupportPlugin
{
    private readonly AppDbContext _db;
    private readonly IConversationStateManager _stateManager;
    private readonly IGitService _gitService;
    private readonly IDeploymentService _deploymentService;
    private readonly ISupervisionNotifier _notifier;
    private readonly ITenantConfigService _tenantConfigService;
    private readonly IncidentAnalysisEngine _analysisEngine;
    private readonly HiveOps.Domain.Interfaces.IEmbeddingService _embeddingService;
    private readonly ITargetDatabaseDiagnosticService _targetDbDiagnostic;
    private readonly ITenantDataFixerService _dataFixer;
    private readonly ILogger<SupportPlugin>? _logger;
    private readonly ITenantContextPackService? _contextPacks;

    public SupportPlugin(
        AppDbContext db,
        IConversationStateManager stateManager,
        IGitService gitService,
        IDeploymentService deploymentService,
        ISupervisionNotifier notifier,
        ITenantConfigService tenantConfigService,
        IncidentAnalysisEngine analysisEngine,
        HiveOps.Domain.Interfaces.IEmbeddingService embeddingService,
        ITargetDatabaseDiagnosticService targetDbDiagnostic,
        ITenantDataFixerService dataFixer,
        ILogger<SupportPlugin>? logger = null,
        ITenantContextPackService? contextPacks = null)
    {
        _db = db;
        _stateManager = stateManager;
        _gitService = gitService;
        _deploymentService = deploymentService;
        _notifier = notifier;
        _tenantConfigService = tenantConfigService;
        _analysisEngine = analysisEngine;
        _embeddingService = embeddingService;
        _targetDbDiagnostic = targetDbDiagnostic;
        _dataFixer = dataFixer;
        _logger = logger;
        _contextPacks = contextPacks;
    }

    [KernelFunction("analyze_incident")]
    [Description("Analyzes a reported incident. Classifies it, checks for missing info, and returns diagnosis or follow-up questions.")]
    public async Task<string> AnalyzeIncidentAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        [Description("User's incident description.")] string description,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        var ctx = await GetOrCreateStateAsync(tenantId, convId, cancellationToken);

        var incident = await _db.Incidents
            .FirstOrDefaultAsync(i => i.ConversationId == convId && i.Status != IncidentStatus.Closed, cancellationToken);

        var isNew = incident is null;
        if (isNew)
        {
            incident = new Incident
            {
                TenantId = tenantId,
                ConversationId = convId,
                Title = description[..Math.Min(description.Length, 120)],
                Description = description,
                Category = ClassifyIncident(description),
                Status = IncidentStatus.InProgress,
                AssignedTo = "bot"
            };
            _db.Incidents.Add(incident);
            await _db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            incident!.Description += $"\n[Follow-up]: {description}";
            incident.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        var analysis = await _analysisEngine.AnalyzeAsync(tenantId, incident!.Description, incident.Id, cancellationToken);
        IncidentTriageApplier.ApplyAnalysis(incident, analysis);

        // When triage is complete and does not require escalation, hand the incident
        // to the auto workflow (it only picks up Status == Open && AssignedTo == "bot").
        if (analysis.MissingInfo.Count == 0 && !analysis.RequiresEscalation)
        {
            incident.Status = IncidentStatus.Open;
            incident.AssignedTo = "bot";
        }

        if (isNew)
        {
            var oldest = await _db.Incidents
                .Where(i => i.TenantId == tenantId
                            && i.Fingerprint == incident.Fingerprint
                            && i.Status != IncidentStatus.Closed
                            && i.Status != IncidentStatus.Resolved)
                .OrderBy(i => i.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (oldest is not null && oldest.Id != incident.Id)
            {
                oldest.Description += $"\n[Merged duplicate {incident.Id}]\n{incident.Description}";
                oldest.OccurrenceCount += incident.OccurrenceCount;
                oldest.UpdatedAt = DateTimeOffset.UtcNow;
                _db.Incidents.Remove(incident);
                incident = oldest;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        ctx.State = ConversationState.InIncidentAnalysis;
        ctx.FlowData["incidentId"] = incident.Id.ToString();
        await _stateManager.SetStateAsync(tenantId, convId, ctx, cancellationToken);

        await _notifier.NotifyIncidentCreatedAsync(tenantId, incident.Id, incident.Title, incident.Severity.ToString(), cancellationToken);

        if (incident.Severity == IncidentSeverity.Critical)
            return $"CRITICAL incident detected (ID: {incident.Id}). Escalating to engineer immediately.";

        if (incident.Status == IncidentStatus.PendingInfo && analysis.MissingInfo.Count > 0)
            return $"Incident {incident.Id} recorded. {incident.FinalUserResponse}";

        return $"Incident {incident.Id} analyzed. {incident.FinalUserResponse}";
    }

    [KernelFunction("query_database_diagnostic")]
    [Description("Runs safe read-only SQL diagnostics against the tenant database. Only SELECT allowed.")]
    public async Task<string> QueryDatabaseDiagnosticAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        [Description("Safe SQL query (SELECT only).")] string sqlQuery,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        var normalized = sqlQuery.Trim();
        if (!normalized.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return "SQL rejected: only read-only SELECT queries are allowed for diagnostics.";

        if (!SafetyValidator.IsSafeSql(normalized))
        {
            var reason = SafetyValidator.GetRejectionReason(normalized);
            return $"SQL rejected for safety: {reason}";
        }

        var incident = await _db.Incidents
            .FirstOrDefaultAsync(i => i.ConversationId == convId && i.Status != IncidentStatus.Closed, cancellationToken);

        TargetQueryResult result;
        try
        {
            result = await _targetDbDiagnostic.ExecuteReadOnlyQueryAsync(tenantId, normalized, cancellationToken);
        }
        catch (Exception ex)
        {
            result = new TargetQueryResult(false, string.Empty, 0, ex.Message);
        }

        if (incident is not null)
        {
            var logResult = $"Query: {normalized}\nSuccess: {result.Success} | Rows: {result.RowCount}\n{TruncateForLog(result.Success ? result.RenderedResult : result.Error ?? "unknown error")}";
            _db.DiagnosticLogs.Add(new DiagnosticLog
            {
                TenantId = tenantId,
                IncidentId = incident.Id,
                StepName = "db_diagnostic",
                Result = logResult,
                IsSuccess = result.Success
            });
            await _db.SaveChangesAsync(cancellationToken);
        }

        if (!result.Success)
            return $"La consulta de diagnóstico falló: {result.Error}";

        var rendered = string.IsNullOrWhiteSpace(result.RenderedResult)
            ? "(sin filas)"
            : result.RenderedResult;
        if (rendered.Length > 1500)
            rendered = rendered[..1500] + "\n... (truncado)";

        return $"Resultado del diagnóstico ({result.RowCount} filas):\n{rendered}";
    }

    [KernelFunction("search_kb")]
    [Description("Searches the tenant knowledge base for articles relevant to a functional or how-to question.")]
    public async Task<string> SearchKbAsync(
        Kernel kernel,
        [Description("Search query.")] string query,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);

        var keywords = query.ToLowerInvariant()
            .Split([' ', ',', '.', ';', ':', '?', '¿', '!', '¡'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .Take(6)
            .ToList();

        var articles = await _db.KbArticles
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.IsPublished)
            .ToListAsync(cancellationToken);

        if (articles.Count == 0)
            return "No encontré artículos en la base de conocimiento.";

        var keywordMatched = articles
            .Where(a => keywords.Count == 0 || keywords.Any(k =>
                a.Title.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                a.Content.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                a.Tags.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var pool = (keywordMatched.Count > 0 ? keywordMatched : articles).Take(40).ToList();

        // Minimum cosine similarity for a semantic match to be considered relevant.
        const double MinCosineSimilarity = 0.25;

        float[]? queryVector = null;
        try
        {
            queryVector = await _embeddingService.GenerateEmbeddingAsync(query, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Embedding service unavailable: fall back to plain keyword ranking.
        }

        List<KbArticle> top = [];
        if (queryVector is { Length: > 0 })
        {
            // Semantic ranking over articles with a stored embedding; weak matches are dropped.
            top = pool
                .Select(a => (Article: a, Score: a.EmbeddingVector is { Length: > 0 } blob
                    ? EmbeddingMath.CosineSimilarity(queryVector, EmbeddingMath.FloatsFromLittleEndianBytes(blob))
                    : double.NegativeInfinity))
                .Where(x => x.Score >= MinCosineSimilarity)
                .OrderByDescending(x => x.Score)
                .Take(3)
                .Select(x => x.Article)
                .ToList();
        }

        if (top.Count == 0)
        {
            // No embedding available or nothing cleared the similarity threshold.
            top = pool
                .OrderByDescending(a => KeywordScore(a, keywords))
                .Take(3)
                .ToList();
        }

        if (top.Count == 0)
            return "No encontré artículos relacionados en la base de conocimiento.";

        var lines = top.Select(a =>
        {
            var body = !string.IsNullOrWhiteSpace(a.ResolutionSteps) ? a.ResolutionSteps : a.Content;
            if (body.Length > 300)
                body = body[..300] + "...";
            return $"• {a.Title}\n{body}";
        });

        return "Esto encontré en la base de conocimiento:\n\n" + string.Join("\n\n", lines);
    }

    [KernelFunction("provide_incident_info")]
    [Description("Appends user-provided follow-up details to an incident pending information and re-runs triage.")]
    public async Task<string> ProvideIncidentInfoAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        [Description("Additional details provided by the user.")] string info,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        var incident = await _db.Incidents
            .FirstOrDefaultAsync(i => i.ConversationId == convId && i.Status != IncidentStatus.Closed, cancellationToken);

        if (incident is null)
            return "No hay un incidente activo para esta conversación.";

        incident.Description += $"\n[User follow-up]: {info}";
        incident.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var analysis = await _analysisEngine.AnalyzeAsync(tenantId, incident.Description, incident.Id, cancellationToken);
        IncidentTriageApplier.ApplyAnalysis(incident, analysis);

        var ctx = await GetOrCreateStateAsync(tenantId, convId, cancellationToken);

        if (analysis.MissingInfo.Count == 0 && !analysis.RequiresEscalation)
        {
            // Triage complete: hand off to the auto workflow (Open + bot).
            incident.Status = IncidentStatus.Open;
            incident.AssignedTo = "bot";
            incident.UpdatedAt = DateTimeOffset.UtcNow;

            ctx.State = ConversationState.InIncidentResolution;
            ctx.FlowData["incidentId"] = incident.Id.ToString();
            await _stateManager.SetStateAsync(tenantId, convId, ctx, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            return $"¡Gracias! Ya tengo toda la información necesaria. Tu incidente #{ShortId(incident.Id)} está siendo procesado y te aviso con las novedades.";
        }

        // Still missing info: keep the incident pending and ask the next question.
        incident.UpdatedAt = DateTimeOffset.UtcNow;
        ctx.State = ConversationState.InIncidentAnalysis;
        ctx.FlowData["incidentId"] = incident.Id.ToString();
        await _stateManager.SetStateAsync(tenantId, convId, ctx, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return !string.IsNullOrWhiteSpace(incident.FinalUserResponse)
            ? incident.FinalUserResponse
            : "Gracias por el detalle. ¿Podrías contarme un poco más sobre el problema?";
    }

    [KernelFunction("get_incident_status")]
    [Description("Lists the open or recent incidents of the conversation with status and latest progress.")]
    public async Task<string> GetIncidentStatusAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);

        var query = _db.Incidents.AsNoTracking().Where(i => i.TenantId == tenantId);
        if (Guid.TryParse(conversationId, out var convId))
            query = query.Where(i => i.ConversationId == convId);

        var incidents = await query
            .OrderByDescending(i => i.UpdatedAt)
            .Take(5)
            .ToListAsync(cancellationToken);

        if (incidents.Count == 0)
            return "No encontré tickets asociados a esta conversación. Si tenés un problema, contame qué está pasando y lo registro.";

        var lines = incidents.Select(i =>
        {
            var progress = !string.IsNullOrWhiteSpace(i.ResolutionNotes)
                ? TruncateForUser(i.ResolutionNotes, 120)
                : !string.IsNullOrWhiteSpace(i.DeployStatus)
                    ? $"Deploy: {TruncateForUser(i.DeployStatus, 80)}"
                    : null;

            return progress is null
                ? $"• #{ShortId(i.Id)} — {MapStatusToSpanish(i.Status)} — {i.Title}"
                : $"• #{ShortId(i.Id)} — {MapStatusToSpanish(i.Status)} — {i.Title} ({progress})";
        });

        return "Estado de tus tickets:\n" + string.Join("\n", lines);
    }

    [KernelFunction("reject_fix")]
    [Description("Rejects the proposed fix and leaves the incident open for review.")]
    public async Task<string> RejectFixAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        [Description("Reason for rejection.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        var incident = await _db.Incidents
            .FirstOrDefaultAsync(i => i.ConversationId == convId && i.Status != IncidentStatus.Closed, cancellationToken);

        var hasPendingScript = incident is not null && await _db.IncidentAttachments
            .AnyAsync(a => a.IncidentId == incident.Id && a.Type == IncidentAttachmentType.SqlScript, cancellationToken);

        if (incident is null || (string.IsNullOrWhiteSpace(incident.GitBranch) && !hasPendingScript))
            return "No hay un fix pendiente de aprobación para este incidente.";

        incident.Status = IncidentStatus.Open;
        incident.AssignedTo = "engineer";
        incident.RequiresHumanApproval = false;
        incident.ResolutionNotes = string.IsNullOrWhiteSpace(incident.ResolutionNotes)
            ? $"Fix rechazado por el usuario: {reason ?? "sin motivo"}"
            : $"{incident.ResolutionNotes}\nFix rechazado por el usuario: {reason ?? "sin motivo"}";
        incident.UpdatedAt = DateTimeOffset.UtcNow;

        var ctx = await GetOrCreateStateAsync(tenantId, convId, cancellationToken);
        ctx.State = ConversationState.Idle;
        await _stateManager.SetStateAsync(tenantId, convId, ctx, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return "Fix rechazado. El incidente quedó abierto para revisión.";
    }

    [KernelFunction("propose_code_fix")]
    [Description("Proposes a code fix, creates a Git branch, and commits the change. Returns branch name and commit hash.")]
    public async Task<string> ProposeCodeFixAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        [Description("Description of the fix.")] string fixDescription,
        [Description("Files to modify (relative paths).")] string[] filePaths,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        var incident = await _db.Incidents
            .FirstOrDefaultAsync(i => i.ConversationId == convId && i.Status != IncidentStatus.Closed, cancellationToken);

        if (incident is null)
            return "No active incident found. Start with analyze_incident first.";

        var branchName = $"support/inc-{incident.Id:N}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var commitMessage = $"[Support Bot] Fix for incident {incident.Id}: {fixDescription}";

        await _gitService.CreateBranchAsync(tenantId, branchName, cancellationToken);
        var commitHash = await _gitService.CommitAsync(tenantId, commitMessage, filePaths, cancellationToken);
        await _gitService.PushAsync(tenantId, branchName, cancellationToken);

        incident.GitBranch = branchName;
        incident.GitCommitHash = commitHash;
        incident.Status = IncidentStatus.InProgress;
        incident.UpdatedAt = DateTimeOffset.UtcNow;

        var ctx = await GetOrCreateStateAsync(tenantId, convId, cancellationToken);
        ctx.State = ConversationState.AwaitingIncidentApproval;
        await _stateManager.SetStateAsync(tenantId, convId, ctx, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyApprovalRequiredAsync(tenantId, incident.Id, branchName, cancellationToken);

        return $"Proposed fix on branch `{branchName}` (commit: {commitHash}). Reply 'approve' to deploy or 'reject' to discard.";
    }

    [KernelFunction("propose_db_fix")]
    [Description("Proposes a database fix script. Validates safety and stores it for review.")]
    public async Task<string> ProposeDbFixAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        [Description("SQL script for the fix.")] string sqlScript,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        if (!SafetyValidator.IsSafeSql(sqlScript))
        {
            var reason = SafetyValidator.GetRejectionReason(sqlScript);
            return $"DB fix rejected: {reason}. Please revise the script.";
        }

        var incident = await _db.Incidents
            .FirstOrDefaultAsync(i => i.ConversationId == convId && i.Status != IncidentStatus.Closed, cancellationToken);

        if (incident is null)
            return "No active incident found.";

        // Attach script as pending review
        _db.IncidentAttachments.Add(new IncidentAttachment
        {
            TenantId = tenantId,
            IncidentId = incident.Id,
            Type = IncidentAttachmentType.SqlScript,
            FileName = $"fix_{incident.Id:N}.sql",
            Content = sqlScript
        });

        var ctx = await GetOrCreateStateAsync(tenantId, convId, cancellationToken);
        ctx.State = ConversationState.AwaitingIncidentApproval;
        await _stateManager.SetStateAsync(tenantId, convId, ctx, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyApprovalRequiredAsync(tenantId, incident.Id, $"db-fix-{incident.Id:N}", cancellationToken);

        return "Database fix script stored for review. Reply 'approve' to execute in a transaction, or 'reject' to discard.";
    }

    [KernelFunction("deploy_fix")]
    [Description("Deploys an approved fix. Requires explicit user approval and green pipeline.")]
    public async Task<string> DeployFixAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        [Description("User approval text. Must contain 'confirmo' or 'approve' explicitly.")] string approvalText,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        var explicitApproval = approvalText.Contains("confirmo", StringComparison.OrdinalIgnoreCase)
            || approvalText.Contains("apruebo", StringComparison.OrdinalIgnoreCase)
            || approvalText.Contains("approve", StringComparison.OrdinalIgnoreCase);

        if (!explicitApproval)
            return "Deployment requires explicit approval. Please reply 'confirmo' or 'approve' to proceed.";

        var incident = await _db.Incidents
            .Include(i => i.Attachments)
            .FirstOrDefaultAsync(i => i.ConversationId == convId && i.Status != IncidentStatus.Closed, cancellationToken);

        if (incident is null)
            return "No active incident found.";

        // Database fix pending approval: execute the stored script via the
        // governed fixer (safety-validated, tenant-scoped connection).
        var pendingScript = incident.Attachments
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault(a => a.Type == IncidentAttachmentType.SqlScript);
        if (pendingScript is not null && incident.Status == IncidentStatus.AwaitingApproval)
        {
            if (!SafetyValidator.IsSafeSql(pendingScript.Content))
            {
                var reason = SafetyValidator.GetRejectionReason(pendingScript.Content);
                return $"El script pendiente no pasó la validación de seguridad: {reason}. Un ingeniero lo revisará.";
            }

            var fixResult = await _dataFixer.ExecuteSafeSqlAsync(
                incident.TenantId, pendingScript.Content, incident.Id, cancellationToken);
            if (!fixResult.Success)
                return $"Falló la ejecución del fix de base de datos: {fixResult.Message}";

            _db.IncidentAttachments.Remove(pendingScript);
            incident.Status = IncidentStatus.Resolved;
            incident.ResolvedAt = DateTimeOffset.UtcNow;
            incident.RequiresHumanApproval = false;
            incident.ResolutionNotes = $"Corrección de base de datos aprobada por el usuario y aplicada ({fixResult.RowsAffected} filas afectadas)";
            incident.UpdatedAt = DateTimeOffset.UtcNow;

            var resetCtx = await GetOrCreateStateAsync(tenantId, convId, cancellationToken);
            resetCtx.State = ConversationState.InIncidentAnalysis;
            await _stateManager.SetStateAsync(tenantId, convId, resetCtx, cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);

            // Best effort: record the successful fix in the tenant context pack.
            await AppendResolutionMemorySafelyAsync(tenantId, incident, succeeded: true, notes: null, cancellationToken);

            return $"Fix aplicado: {fixResult.RowsAffected} registro(s) actualizados. ¿Podés confirmar que el problema quedó resuelto?";
        }

        if (string.IsNullOrWhiteSpace(incident.GitBranch) || string.IsNullOrWhiteSpace(incident.GitCommitHash))
            return "No code fix has been proposed for this incident.";

        if (!await TenantDeployPolicy.IsManualDeployAllowedAsync(_tenantConfigService, incident.TenantId, cancellationToken))
            return "Despliegue deshabilitado para este tenant en la configuración DeployGit (ManualDeployAllowed).";

        var pipelineHealthy = await _deploymentService.IsPipelineHealthyAsync(cancellationToken);
        if (!pipelineHealthy)
            return "Pipeline is not healthy. Deployment blocked until pipeline stabilizes.";

        var deployId = await _deploymentService.TriggerDeployAsync(incident.GitBranch, incident.GitCommitHash, cancellationToken);
        incident.DeployStatus = deployId;
        incident.Status = IncidentStatus.Resolved;
        incident.ResolvedAt = DateTimeOffset.UtcNow;
        incident.UpdatedAt = DateTimeOffset.UtcNow;

        var ctx = await GetOrCreateStateAsync(tenantId, convId, cancellationToken);
        ctx.State = ConversationState.Completed;
        ctx.FlowData.Remove("incidentId");
        await _stateManager.SetStateAsync(tenantId, convId, ctx, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        // Best effort: record the successful fix in the tenant context pack.
        await AppendResolutionMemorySafelyAsync(tenantId, incident, succeeded: true, notes: null, cancellationToken);

        return $"Deployment triggered: {deployId}. Incident {incident.Id} marked as resolved.";
    }

    [KernelFunction("escalate_to_engineer")]
    [Description("Escalates the incident to a human engineer. Updates status and logs the reason.")]
    public async Task<string> EscalateToEngineerAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        [Description("Reason for escalation.")] string reason,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        var incident = await _db.Incidents
            .FirstOrDefaultAsync(i => i.ConversationId == convId && i.Status != IncidentStatus.Closed, cancellationToken);

        if (incident is not null)
        {
            incident.AssignedTo = "engineer";
            incident.Status = IncidentStatus.InProgress;
            incident.ResolutionNotes = $"Escalated: {reason}";
            incident.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return $"Incident escalated to engineer. Reason: {reason}. A human will review shortly.";
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Best-effort resolution-memory append into the tenant context pack.
    /// Failures are logged but never break the resolve/escalate flow.
    /// </summary>
    private async Task AppendResolutionMemorySafelyAsync(
        Guid tenantId, Incident incident, bool succeeded, string? notes, CancellationToken ct)
    {
        if (_contextPacks is null) return;
        try
        {
            await _contextPacks.AppendResolutionMemoryAsync(tenantId, incident, succeeded, notes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex,
                "Context-pack resolution memory append failed for incident {IncidentId} (tenant {TenantId}).",
                incident.Id, tenantId);
        }
    }

    private static IncidentCategory ClassifyIncident(string description)
    {
        var text = description.ToLowerInvariant();
        if (text.Contains("sql") || text.Contains("base de datos") || text.Contains("db") || text.Contains("tabla") || text.Contains("columna"))
            return IncidentCategory.Database;
        if (text.Contains("deploy") || text.Contains("servidor") || text.Contains("infra") || text.Contains("redis") || text.Contains("network"))
            return IncidentCategory.Infrastructure;
        if (text.Contains("bug") || text.Contains("código") || text.Contains("codigo") || text.Contains("error") || text.Contains("exception") || text.Contains("crash"))
            return IncidentCategory.Code;
        return IncidentCategory.Other;
    }

    private async Task<ConversationStateContext> GetOrCreateStateAsync(
        Guid tenantId, Guid conversationId, CancellationToken ct)
    {
        return await _stateManager.GetStateAsync(tenantId, conversationId, ct)
            ?? new ConversationStateContext { TenantId = tenantId, ConversationId = conversationId };
    }

    private static double KeywordScore(KbArticle article, IReadOnlyList<string> keywords)
    {
        var text = $"{article.Title}\n{article.Content}\n{article.Tags}".ToLowerInvariant();
        return keywords.Count(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static string ShortId(Guid id) => id.ToString()[..8];

    private static string MapStatusToSpanish(IncidentStatus status) => status switch
    {
        IncidentStatus.Open => "Abierto (en cola de diagnóstico)",
        IncidentStatus.InProgress => "En progreso",
        IncidentStatus.PendingInfo => "Esperando más información tuya",
        IncidentStatus.AwaitingApproval => "Esperando aprobación del fix",
        IncidentStatus.Resolved => "Resuelto (esperando tu confirmación)",
        IncidentStatus.Closed => "Cerrado",
        _ => status.ToString()
    };

    private static string TruncateForLog(string? text, int max = 4000)
        => string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max] + "…";

    private static string TruncateForUser(string? text, int max)
        => string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max] + "…";

    private static Guid GetTenantId(Kernel kernel)
    {
        if (kernel.Data.TryGetValue(KernelConstants.TenantIdKey, out var val) && val is Guid g && g != Guid.Empty)
            return g;

        throw new InvalidOperationException("TenantId is required for tenant-scoped plugin execution.");
    }
}
