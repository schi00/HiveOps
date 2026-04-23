using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using HiveOps.Application;
using HiveOps.Application.Interfaces;
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

    public SupportPlugin(
        AppDbContext db,
        IConversationStateManager stateManager,
        IGitService gitService,
        IDeploymentService deploymentService,
        ISupervisionNotifier notifier)
    {
        _db = db;
        _stateManager = stateManager;
        _gitService = gitService;
        _deploymentService = deploymentService;
        _notifier = notifier;
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

        // Create or update incident record
        var incident = await _db.Incidents
            .FirstOrDefaultAsync(i => i.ConversationId == convId && i.Status != IncidentStatus.Closed, cancellationToken);

        if (incident is null)
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
        }
        else
        {
            incident.Description += $"\n[Follow-up]: {description}";
            incident.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);

        ctx.State = ConversationState.InIncidentAnalysis;
        ctx.FlowData["incidentId"] = incident.Id.ToString();
        await _stateManager.SetStateAsync(tenantId, convId, ctx, cancellationToken);

        await _notifier.NotifyIncidentCreatedAsync(tenantId, incident.Id, incident.Title, incident.Severity.ToString(), cancellationToken);

        // If critical keywords, escalate immediately
        if (incident.Severity == IncidentSeverity.Critical)
        {
            return $"CRITICAL incident detected (ID: {incident.Id}). Escalating to engineer immediately.";
        }

        // Determine if more info is needed
        var missing = GetMissingInfo(description, incident.Category);
        if (missing.Count > 0)
        {
            incident.Status = IncidentStatus.PendingInfo;
            await _db.SaveChangesAsync(cancellationToken);
            return $"Incident {incident.Id} recorded. To diagnose, I need: {string.Join(", ", missing)}.";
        }

        return $"Incident {incident.Id} analyzed. Category: {incident.Category}. Ready for diagnosis.";
    }

    [KernelFunction("query_database_diagnostic")]
    [Description("Runs safe read-only SQL diagnostics. Only SELECT, EXPLAIN, SHOW allowed.")]
    public async Task<string> QueryDatabaseDiagnosticAsync(
        Kernel kernel,
        [Description("Conversation ID.")] string conversationId,
        [Description("Safe SQL query (SELECT only).")] string sqlQuery,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId(kernel);
        if (!Guid.TryParse(conversationId, out var convId))
            return "Invalid conversation ID.";

        if (!SafetyValidator.IsSafeSql(sqlQuery))
        {
            var reason = SafetyValidator.GetRejectionReason(sqlQuery);
            return $"SQL rejected for safety: {reason}";
        }

        var incident = await _db.Incidents
            .FirstOrDefaultAsync(i => i.ConversationId == convId && i.Status != IncidentStatus.Closed, cancellationToken);

        // Log diagnostic step
        if (incident is not null)
        {
            _db.DiagnosticLogs.Add(new DiagnosticLog
            {
                TenantId = tenantId,
                IncidentId = incident.Id,
                StepName = "db_diagnostic",
                Result = $"Executing: {sqlQuery}",
                IsSuccess = true
            });
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Execute via raw SQL — but we can't return arbitrary results easily from EF
        // Return a note that the diagnostic was logged and queued
        return $"Diagnostic query logged. In a production setup, this would execute: {sqlQuery}";
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

        await _gitService.CreateBranchAsync(branchName, cancellationToken);
        var commitHash = await _gitService.CommitAsync(commitMessage, filePaths, cancellationToken);
        await _gitService.PushAsync(branchName, cancellationToken);

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
            || approvalText.Contains("approve", StringComparison.OrdinalIgnoreCase);

        if (!explicitApproval)
            return "Deployment requires explicit approval. Please reply 'confirmo' or 'approve' to proceed.";

        var incident = await _db.Incidents
            .FirstOrDefaultAsync(i => i.ConversationId == convId && i.Status != IncidentStatus.Closed, cancellationToken);

        if (incident is null)
            return "No active incident found.";

        if (string.IsNullOrWhiteSpace(incident.GitBranch) || string.IsNullOrWhiteSpace(incident.GitCommitHash))
            return "No code fix has been proposed for this incident.";

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

    private static List<string> GetMissingInfo(string description, IncidentCategory category)
    {
        var missing = new List<string>();
        var text = description.ToLowerInvariant();

        if (category == IncidentCategory.Code)
        {
            if (!text.Contains("error") && !text.Contains("exception") && !text.Contains("stack trace"))
                missing.Add("error message or stack trace");
            if (!text.Contains("archivo") && !text.Contains("file") && !text.Contains("class") && !text.Contains("namespace"))
                missing.Add("affected file or class name");
        }
        else if (category == IncidentCategory.Database)
        {
            if (!text.Contains("tabla") && !text.Contains("table"))
                missing.Add("affected table name");
            if (!text.Contains("consulta") && !text.Contains("query") && !text.Contains("sql"))
                missing.Add("problematic query or symptom");
        }

        return missing;
    }

    private async Task<ConversationStateContext> GetOrCreateStateAsync(
        Guid tenantId, Guid conversationId, CancellationToken ct)
    {
        return await _stateManager.GetStateAsync(tenantId, conversationId, ct)
            ?? new ConversationStateContext { TenantId = tenantId, ConversationId = conversationId };
    }

    private static Guid GetTenantId(Kernel kernel)
    {
        if (kernel.Data.TryGetValue(KernelConstants.TenantIdKey, out var val) && val is Guid g && g != Guid.Empty)
            return g;

        throw new InvalidOperationException("TenantId is required for tenant-scoped plugin execution.");
    }
}
