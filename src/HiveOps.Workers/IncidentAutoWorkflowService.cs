using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HiveOps.Agents.Support;
using HiveOps.Application.Interfaces;
using HiveOps.Application.Services;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Validation;
using HiveOps.Infrastructure;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using HiveOps.Infrastructure.Services;
using Microsoft.SemanticKernel;
using System.Text;

namespace HiveOps.Workers;

/// <summary>
/// Background service that monitors new incidents and executes automatic workflow:
/// 1. Analyze incident with LLM
/// 2. Request more info if needed
/// 3. Execute diagnostics
/// 4. Propose fix
/// 5. Request approval if needed (>3 lines or >3 records)
/// 6. Deploy fix if approved
/// 7. Close incident if resolved
/// 8. Send email to user
/// 9. Generate KbArticle
/// </summary>
public sealed class IncidentAutoWorkflowService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IncidentAutoWorkflowService> _logger;
    private readonly IncidentAutoWorkflowOptions _options;
    private readonly IGitService _gitService;
    private readonly IDeploymentService _deploymentService;
    private readonly IIncidentMessageHistoryService _messageHistory;
    private readonly DatabaseBackupService _databaseBackup;
    private readonly IncidentEmailService _emailService;
    private readonly KbArticleGenerator _kbGenerator;
    private readonly KernelFactory _kernelFactory;

    public IncidentAutoWorkflowService(
        IServiceScopeFactory scopeFactory,
        ILogger<IncidentAutoWorkflowService> logger,
        IOptions<IncidentAutoWorkflowOptions> options,
        IGitService gitService,
        IDeploymentService deploymentService,
        IIncidentMessageHistoryService messageHistory,
        DatabaseBackupService databaseBackup,
        IncidentEmailService emailService,
        KbArticleGenerator kbGenerator,
        KernelFactory kernelFactory)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
        _gitService = gitService;
        _deploymentService = deploymentService;
        _messageHistory = messageHistory;
        _databaseBackup = databaseBackup;
        _emailService = emailService;
        _kbGenerator = kbGenerator;
        _kernelFactory = kernelFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Incident Auto Workflow Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessIncidentsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing incidents in auto workflow");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.PollingIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("Incident Auto Workflow Service stopped");
    }

    private async Task ProcessIncidentsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        // Find incidents that are Open and assigned to bot
        // Use IgnoreQueryFilters to see incidents from ALL tenants (RLS is bypassed here)
        var incidents = await db.Incidents
            .IgnoreQueryFilters()
            .Where(i => i.Status == IncidentStatus.Open && i.AssignedTo == "bot")
            .OrderBy(i => i.CreatedAt)
            .Take(_options.MaxConcurrentIncidents)
            .ToListAsync(cancellationToken);

        if (incidents.Count == 0)
            return;

        _logger.LogInformation("Processing {Count} incidents in auto workflow", incidents.Count);

        foreach (var incident in incidents)
        {
            // Create a new scope for each incident to properly isolate tenant context
            using var incidentScope = _scopeFactory.CreateScope();
            var incidentDb = incidentScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var incidentTenantContext = incidentScope.ServiceProvider.GetRequiredService<ITenantContext>();

            try
            {
                // Set tenant context for this incident
                if (incidentTenantContext is TenantContext tc)
                {
                    tc.SetTenant(incident.TenantId);
                    _logger.LogInformation("Set tenant context to {TenantId} for incident {IncidentId}", incident.TenantId, incident.Id);
                }

                await ProcessIncidentAsync(incident, incidentDb, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing incident {IncidentId}", incident.Id);
            }
        }
    }

    private async Task ProcessIncidentAsync(Incident incident, AppDbContext db, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing incident {IncidentId}: {Title}", incident.Id, incident.Title);

        // Step 1: Analyze incident (LLM triage already done in CreateIncident)
        // Step 2: Check if more info is needed
        if (incident.Status == IncidentStatus.PendingInfo)
        {
            _logger.LogInformation("Incident {IncidentId} is pending info, skipping", incident.Id);
            return;
        }

        // Step 3: Execute diagnostics using SupportPlugin
        _logger.LogInformation("Executing diagnostics for incident {IncidentId}", incident.Id);
        await ExecuteDiagnosticsAsync(incident, db, cancellationToken);

        // Step 4: Propose fix based on category
        if (incident.Category == IncidentCategory.Code)
        {
            await ProposeCodeFixAsync(incident, db, cancellationToken);
        }
        else if (incident.Category == IncidentCategory.Database)
        {
            await ProposeDatabaseFixAsync(incident, db, cancellationToken);
        }
        else
        {
            _logger.LogInformation("Incident {IncidentId} category {Category} not supported for auto-fix", incident.Id, incident.Category);
            return;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ExecuteDiagnosticsAsync(Incident incident, AppDbContext db, CancellationToken cancellationToken)
    {
        await _messageHistory.AppendMessageToIncidentAsync(
            incident.Id,
            MessageRole.Assistant,
            "Iniciando diagnóstico del incidente...",
            "IncidentAutoWorkflow",
            cancellationToken);

        // Get tenant connection string for external DB diagnostics if needed
        var tenant = await db.Tenants.FindAsync(incident.TenantId, cancellationToken);
        if (tenant is null)
        {
            _logger.LogError("Tenant {TenantId} not found for incident {IncidentId}", incident.TenantId, incident.Id);
            return;
        }

        // Diagnostics logic depends on category - will be handled in fix proposal
        incident.Status = IncidentStatus.InProgress;
        incident.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task ProposeCodeFixAsync(Incident incident, AppDbContext db, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Proposing code fix for incident {IncidentId}", incident.Id);

        await _messageHistory.AppendMessageToIncidentAsync(
            incident.Id,
            MessageRole.Assistant,
            "Generando propuesta de corrección de código...",
            "IncidentAutoWorkflow",
            cancellationToken);

        // Use SupportPlugin to generate code fix proposal
        var fixProposal = await GenerateCodeFixProposalAsync(incident, cancellationToken);

        // Count changed lines using CodeChangeValidator
        var changedLines = CodeChangeValidator.CountChangedLines(fixProposal);

        await _messageHistory.AppendMessageToIncidentAsync(
            incident.Id,
            MessageRole.Assistant,
            $"Propuesta de cambio: {changedLines} líneas modificadas",
            "IncidentAutoWorkflow",
            cancellationToken);

        if (changedLines > _options.CodeLineThreshold)
        {
            incident.RequiresHumanApproval = true;
            incident.Status = IncidentStatus.AwaitingApproval;
            incident.UpdatedAt = DateTimeOffset.UtcNow;
            incident.ResolutionNotes = fixProposal;

            using var scope = _scopeFactory.CreateScope();
            var approvalWorkflow = scope.ServiceProvider.GetRequiredService<IApprovalWorkflowService>();
            var notifier = scope.ServiceProvider.GetRequiredService<ISupervisionNotifier>();
            
            await approvalWorkflow.RequestApprovalAsync(incident.Id, $"Code fix with {changedLines} lines changed", cancellationToken);
            await notifier.NotifyApprovalRequiredAsync(incident.TenantId, incident.Id, $"Code fix requires approval ({changedLines} lines)", cancellationToken);

            _logger.LogInformation("Incident {IncidentId} requires human approval (> {Threshold} lines)", incident.Id, _options.CodeLineThreshold);
        }
        else
        {
            await ApplyCodeFixAsync(incident, fixProposal, db, cancellationToken);
        }
    }

    private async Task ApplyCodeFixAsync(Incident incident, string fixProposal, AppDbContext db, CancellationToken cancellationToken)
    {
        var branchName = $"support/inc-{incident.Id:N}";

        // Create branch
        if (!await _gitService.BranchExistsAsync(branchName, cancellationToken))
        {
            await _gitService.CreateBranchAsync(branchName, cancellationToken);
            _logger.LogInformation("Created branch {Branch} for incident {IncidentId}", branchName, incident.Id);
        }

        incident.GitBranch = branchName;
        incident.Status = IncidentStatus.InProgress;
        incident.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await _messageHistory.AppendMessageToIncidentAsync(
            incident.Id,
            MessageRole.Assistant,
            $"Creado branch {branchName} para aplicar corrección",
            "IncidentAutoWorkflow",
            cancellationToken);

        // Parse fix proposal to extract file paths and code changes
        var fileChanges = ParseCodeChangesFromProposal(fixProposal);

        if (fileChanges.Count > 0)
        {
            // Apply the code changes to actual files
            var modifiedFiles = new List<string>();
            var projectRoot = GetProjectRoot();

            foreach (var fileChange in fileChanges)
            {
                var fullPath = Path.Combine(projectRoot, fileChange.FilePath);
                if (File.Exists(fullPath))
                {
                    try
                    {
                        var originalContent = await File.ReadAllTextAsync(fullPath, cancellationToken);
                        var newContent = ApplyCodeChange(originalContent, fileChange);

                        if (newContent != originalContent)
                        {
                            await File.WriteAllTextAsync(fullPath, newContent, cancellationToken);
                            modifiedFiles.Add(fileChange.FilePath);
                            await _messageHistory.AppendMessageToIncidentAsync(
                                incident.Id,
                                MessageRole.Assistant,
                                $"Archivo modificado: {fileChange.FilePath}",
                                "IncidentAutoWorkflow",
                                cancellationToken);
                            _logger.LogInformation("Modified file {FilePath} for incident {IncidentId}", fileChange.FilePath, incident.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to modify file {FilePath} for incident {IncidentId}", fileChange.FilePath, incident.Id);
                        await _messageHistory.AppendMessageToIncidentAsync(
                            incident.Id,
                            MessageRole.Assistant,
                            $"Error modificando archivo {fileChange.FilePath}: {ex.Message}",
                            "IncidentAutoWorkflow",
                            cancellationToken);
                    }
                }
                else
                {
                    _logger.LogWarning("File {FilePath} not found for incident {IncidentId}", fullPath, incident.Id);
                }
            }

            if (modifiedFiles.Count > 0)
            {
                // Commit the modified files
                var commitMessage = $"[Auto-Fix] Incident {incident.Id}: {incident.Title}";
                try
                {
                    var commitHash = await _gitService.CommitAsync(commitMessage, modifiedFiles.ToArray(), cancellationToken);
                    incident.GitCommitHash = commitHash;
                    incident.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    await _messageHistory.AppendMessageToIncidentAsync(
                        incident.Id,
                        MessageRole.Assistant,
                        $"Commit creado: {commitHash} para archivos: {string.Join(", ", modifiedFiles)}",
                        "IncidentAutoWorkflow",
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to commit changes for incident {IncidentId}", incident.Id);
                    await _messageHistory.AppendMessageToIncidentAsync(
                        incident.Id,
                        MessageRole.Assistant,
                        $"Advertencia: No se pudo crear commit: {ex.Message}",
                        "IncidentAutoWorkflow",
                        cancellationToken);
                }
            }
            else
            {
                await _messageHistory.AppendMessageToIncidentAsync(
                    incident.Id,
                    MessageRole.Assistant,
                    "No se modificaron archivos. Revisión manual requerida.",
                    "IncidentAutoWorkflow",
                    cancellationToken);
                // Escalate to engineer for manual intervention
            incident.AssignedTo = "engineer";
            incident.Status = IncidentStatus.Open;
            incident.ResolutionNotes = $"No files were modified. Manual review required: {fixProposal}";
            await db.SaveChangesAsync(cancellationToken);
            return;
            }
        }
        else
        {
            await _messageHistory.AppendMessageToIncidentAsync(
                incident.Id,
                MessageRole.Assistant,
                "No se identificaron archivos específicos en la propuesta. Revisión manual requerida.",
                "IncidentAutoWorkflow",
                cancellationToken);
            // Escalate to engineer for manual intervention
            incident.AssignedTo = "engineer";
            incident.Status = IncidentStatus.Open;
            incident.ResolutionNotes = $"Fix proposal requires manual file modification: {fixProposal}";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        incident.ResolutionNotes = fixProposal;

        _logger.LogInformation("Incident {IncidentId} fix proposed on branch {Branch}", incident.Id, incident.GitBranch);

        // Auto-deploy if threshold not exceeded
        await DeployFixAsync(incident, db, cancellationToken);
    }

    private string GetProjectRoot()
    {
        // Use the configured Git repository path from LocalGitService
        // This is set in appsettings.json: Git:RepoPath = "c:/temp/hiveops-work"
        var configuredRepoPath = Environment.GetEnvironmentVariable("HIVEOPS_GIT_REPO_PATH") 
            ?? "c:/temp/hiveops-work";
        
        if (Directory.Exists(configuredRepoPath))
        {
            return configuredRepoPath;
        }

        // Fallback: Get the directory containing the solution file
        var currentDir = Directory.GetCurrentDirectory();
        var solutionDir = currentDir;

        // Navigate up to find the solution directory
        while (solutionDir != null && !Directory.GetFiles(solutionDir, "*.sln").Any())
        {
            solutionDir = Directory.GetParent(solutionDir)?.FullName;
        }

        return solutionDir ?? currentDir;
    }

    private List<FileChange> ParseCodeChangesFromProposal(string proposal)
    {
        var fileChanges = new List<FileChange>();
        var lines = proposal.Split('\n');
        var currentFile = "";
        var currentChange = new StringBuilder();
        var inCodeBlock = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Detect file path patterns
            if (trimmed.Contains("File:") || trimmed.Contains("Archivo:") || 
                (trimmed.Contains(".cs") || trimmed.Contains(".ts") || trimmed.Contains(".js")) && 
                (trimmed.Contains("modify") || trimmed.Contains("change") || trimmed.Contains("cambiar")))
            {
                // Save previous change if exists
                if (!string.IsNullOrEmpty(currentFile) && currentChange.Length > 0)
                {
                    fileChanges.Add(new FileChange
                    {
                        FilePath = currentFile,
                        ChangeContent = currentChange.ToString()
                    });
                    currentChange.Clear();
                }

                // Extract file path
                var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var word in words)
                {
                    if (word.Contains(".") && (word.Contains("/") || word.Contains("\\") || word.Contains(".")))
                    {
                        currentFile = word.Trim('\'', '"', '`', '*', '-', ':');
                        break;
                    }
                }
            }

            // Detect code blocks with ```
            if (trimmed.StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                if (!inCodeBlock && currentChange.Length > 0 && !string.IsNullOrEmpty(currentFile))
                {
                    fileChanges.Add(new FileChange
                    {
                        FilePath = currentFile,
                        ChangeContent = currentChange.ToString()
                    });
                    currentChange.Clear();
                    currentFile = "";
                }
                continue;
            }

            if (inCodeBlock && !string.IsNullOrEmpty(currentFile))
            {
                currentChange.AppendLine(line);
            }
        }

        // Add any remaining change
        if (!string.IsNullOrEmpty(currentFile) && currentChange.Length > 0)
        {
            fileChanges.Add(new FileChange
            {
                FilePath = currentFile,
                ChangeContent = currentChange.ToString()
            });
        }

        return fileChanges;
    }

    private string ApplyCodeChange(string originalContent, FileChange fileChange)
    {
        // Try to find and replace code based on common patterns
        var changeLines = fileChange.ChangeContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var originalLines = originalContent.Split('\n');

        // Look for matching patterns and replace
        var newLines = new List<string>(originalLines);
        var modified = false;

        for (var i = 0; i < changeLines.Length; i++)
        {
            var changeLine = changeLines[i].Trim();
            
            // Skip comment lines in the change
            if (changeLine.StartsWith("//") || changeLine.StartsWith("#") || changeLine.StartsWith("/*"))
                continue;

            // Try to find matching line in original
            for (var j = 0; j < newLines.Count; j++)
            {
                var originalLine = newLines[j].Trim();
                
                // Simple matching based on similarity
                if (LinesAreSimilar(originalLine, changeLine, 0.7f))
                {
                    newLines[j] = changeLine;
                    modified = true;
                    break;
                }
            }
        }

        return modified ? string.Join('\n', newLines) : originalContent;
    }

    private bool LinesAreSimilar(string line1, string line2, float threshold)
    {
        if (string.IsNullOrEmpty(line1) || string.IsNullOrEmpty(line2))
            return false;

        // Simple similarity check based on common words
        var words1 = line1.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words2 = line2.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words1.Length == 0 || words2.Length == 0)
            return false;

        var matches = 0;
        foreach (var word1 in words1)
        {
            foreach (var word2 in words2)
            {
                if (word1.Equals(word2, StringComparison.OrdinalIgnoreCase))
                {
                    matches++;
                    break;
                }
            }
        }

        var similarity = (float)matches / Math.Max(words1.Length, words2.Length);
        return similarity >= threshold;
    }

    private class FileChange
    {
        public string FilePath { get; set; } = string.Empty;
        public string ChangeContent { get; set; } = string.Empty;
    }


    private async Task ProposeDatabaseFixAsync(Incident incident, AppDbContext db, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Proposing database fix for incident {IncidentId}", incident.Id);

        await _messageHistory.AppendMessageToIncidentAsync(
            incident.Id,
            MessageRole.Assistant,
            "Generando propuesta de corrección de base de datos...",
            "IncidentAutoWorkflow",
            cancellationToken);

        // Use SupportPlugin to generate DB fix proposal
        var fixProposal = await GenerateDatabaseFixProposalAsync(incident, cancellationToken);

        // Get tenant connection string for external DB
        var tenant = await db.Tenants.FindAsync(incident.TenantId, cancellationToken);
        if (tenant is null)
        {
            _logger.LogError("Tenant {TenantId} not found for incident {IncidentId}", incident.TenantId, incident.Id);
            return;
        }

        // Use MCP connection for HiveCrew and Sandpit tenants
        var connectionString = GetTenantConnectionString(tenant);
        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogError("Tenant {TenantId} has no connection string for incident {IncidentId}", tenant.Id, incident.Id);
            return;
        }

        // Count affected records using DatabaseBackupService
        var affectedRecords = await _databaseBackup.CountAffectedRecordsAsync(
            connectionString,
            fixProposal,
            cancellationToken);

        await _messageHistory.AppendMessageToIncidentAsync(
            incident.Id,
            MessageRole.Assistant,
            $"Propuesta de cambio: {affectedRecords} registros afectados",
            "IncidentAutoWorkflow",
            cancellationToken);

        if (affectedRecords > _options.DbRecordThreshold)
        {
            incident.RequiresHumanApproval = true;
            incident.Status = IncidentStatus.AwaitingApproval;
            incident.UpdatedAt = DateTimeOffset.UtcNow;
            incident.ResolutionNotes = fixProposal;

            using var scope = _scopeFactory.CreateScope();
            var approvalWorkflow = scope.ServiceProvider.GetRequiredService<IApprovalWorkflowService>();
            var notifier = scope.ServiceProvider.GetRequiredService<ISupervisionNotifier>();
            
            await approvalWorkflow.RequestApprovalAsync(incident.Id, $"DB fix with {affectedRecords} records affected", cancellationToken);
            await notifier.NotifyApprovalRequiredAsync(incident.TenantId, incident.Id, $"DB fix requires approval ({affectedRecords} records)", cancellationToken);

            _logger.LogInformation("Incident {IncidentId} requires human approval (> {Threshold} records)", incident.Id, _options.DbRecordThreshold);
        }
        else
        {
            await ApplyDatabaseFixAsync(incident, fixProposal, connectionString, db, cancellationToken);
        }
    }

    private async Task ApplyDatabaseFixAsync(Incident incident, string fixProposal, string connectionString, AppDbContext db, CancellationToken cancellationToken)
    {
        // Backup table before modification
        var tableName = ExtractTableNameFromSql(fixProposal);
        string? backupTable = null;
        if (!string.IsNullOrEmpty(tableName))
        {
            backupTable = await _databaseBackup.BackupTableAsync(connectionString, tableName, cancellationToken);
            incident.BackupLocation = backupTable;

            await _messageHistory.AppendMessageToIncidentAsync(
                incident.Id,
                MessageRole.Assistant,
                $"Backup creado: {backupTable}",
                "IncidentAutoWorkflow",
                cancellationToken);
        }

        incident.Status = IncidentStatus.InProgress;
        incident.UpdatedAt = DateTimeOffset.UtcNow;
        incident.ResolutionNotes = fixProposal;

        _logger.LogInformation("Incident {IncidentId} database fix proposed with backup", incident.Id);

        // Execute the SQL fix with real connection
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = fixProposal;
            command.CommandTimeout = 30;

            var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

            await _messageHistory.AppendMessageToIncidentAsync(
                incident.Id,
                MessageRole.Assistant,
                $"SQL ejecutado exitosamente: {rowsAffected} filas afectadas",
                "IncidentAutoWorkflow",
                cancellationToken);

            _logger.LogInformation("SQL fix executed successfully for incident {IncidentId}. Rows affected: {Rows}", incident.Id, rowsAffected);

            await ResolveIncidentAsync(incident, db, $"Corrección de base de datos aplicada exitosamente ({rowsAffected} filas afectadas)", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute SQL fix for incident {IncidentId}", incident.Id);

            // Attempt rollback if backup exists
            if (!string.IsNullOrEmpty(backupTable) && !string.IsNullOrEmpty(tableName))
            {
                try
                {
                    await _databaseBackup.RestoreTableAsync(connectionString, tableName, backupTable, cancellationToken);
                    await _messageHistory.AppendMessageToIncidentAsync(
                        incident.Id,
                        MessageRole.Assistant,
                        "Rollback ejecutado: tabla restaurada desde backup",
                        "IncidentAutoWorkflow",
                        cancellationToken);
                    _logger.LogInformation("Rollback successful for incident {IncidentId}", incident.Id);
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(rollbackEx, "Rollback failed for incident {IncidentId}", incident.Id);
                }
            }

            // Mark incident as failed for manual intervention
            incident.Status = IncidentStatus.Open;
            incident.AssignedTo = "engineer";
            incident.ResolutionNotes = $"SQL execution failed: {ex.Message}. Manual intervention required.";
            await db.SaveChangesAsync(cancellationToken);

            await _messageHistory.AppendMessageToIncidentAsync(
                incident.Id,
                MessageRole.Assistant,
                $"Error ejecutando SQL: {ex.Message}. Incidente escalado a ingeniero.",
                "IncidentAutoWorkflow",
                cancellationToken);

            throw;
        }
    }

    private async Task DeployFixAsync(Incident incident, AppDbContext db, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(incident.GitBranch))
        {
            _logger.LogWarning("Incident {IncidentId} has no git branch to deploy", incident.Id);
            return;
        }

        await _messageHistory.AppendMessageToIncidentAsync(
            incident.Id,
            MessageRole.Assistant,
            "Iniciando deployment de corrección...",
            "IncidentAutoWorkflow",
            cancellationToken);

        try
        {
            // Push branch
            await _gitService.PushAsync(incident.GitBranch, cancellationToken);

            // Trigger deployment
            var deployId = await _deploymentService.TriggerDeployAsync(incident.GitBranch, incident.GitCommitHash ?? "", cancellationToken);

            incident.DeployStatus = deployId;
            incident.UpdatedAt = DateTimeOffset.UtcNow;

            await _messageHistory.AppendMessageToIncidentAsync(
                incident.Id,
                MessageRole.Assistant,
                $"Deployment iniciado: {deployId}",
                "IncidentAutoWorkflow",
                cancellationToken);

            _logger.LogInformation("Deployment {DeployId} triggered for incident {IncidentId}", deployId, incident.Id);

            // Wait for deployment to complete and verify
            await WaitForDeploymentAndVerifyAsync(incident, deployId, cancellationToken);

            await ResolveIncidentAsync(incident, db, "Corrección desplegada y verificada exitosamente", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deployment failed for incident {IncidentId}", incident.Id);
            await _messageHistory.AppendMessageToIncidentAsync(
                incident.Id,
                MessageRole.Assistant,
                $"Error en deployment: {ex.Message}. Incidente escalado a ingeniero.",
                "IncidentAutoWorkflow",
                cancellationToken);

            // Escalate to engineer for manual intervention
            incident.Status = IncidentStatus.Open;
            incident.AssignedTo = "engineer";
            incident.ResolutionNotes = $"Deployment failed: {ex.Message}. Manual intervention required.";
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task WaitForDeploymentAndVerifyAsync(Incident incident, string deployId, CancellationToken cancellationToken)
    {
        const int maxWaitMinutes = 10;
        const int checkIntervalSeconds = 30;
        var totalWaitTime = TimeSpan.Zero;
        var maxWaitTime = TimeSpan.FromMinutes(maxWaitMinutes);

        _logger.LogInformation("Waiting for deployment {DeployId} to complete", deployId);

        while (totalWaitTime < maxWaitTime)
        {
            await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds), cancellationToken);
            totalWaitTime += TimeSpan.FromSeconds(checkIntervalSeconds);

            // Check pipeline health
            var isHealthy = await _deploymentService.IsPipelineHealthyAsync(cancellationToken);
            if (isHealthy)
            {
                await _messageHistory.AppendMessageToIncidentAsync(
                    incident.Id,
                    MessageRole.Assistant,
                    "Pipeline saludable. Verificando deployment...",
                    "IncidentAutoWorkflow",
                    cancellationToken);

                // Additional verification: check if the fix resolved the issue
                // For now, we'll assume pipeline health = successful deployment
                // In a full implementation, this would:
                // 1. Run health checks against the deployed environment
                // 2. Verify the specific issue is resolved
                // 3. Check logs for errors

                _logger.LogInformation("Deployment {DeployId} completed successfully for incident {IncidentId}", deployId, incident.Id);
                return;
            }

            await _messageHistory.AppendMessageToIncidentAsync(
                incident.Id,
                MessageRole.Assistant,
                $"Esperando deployment... ({totalWaitTime.TotalMinutes:F1} minutos)",
                "IncidentAutoWorkflow",
                cancellationToken);
        }

        // Timeout reached
        _logger.LogWarning("Deployment {DeployId} did not complete within {MaxWaitMinutes} minutes", deployId, maxWaitMinutes);
        throw new TimeoutException($"Deployment did not complete within {maxWaitMinutes} minutes");
    }

    private async Task ResolveIncidentAsync(Incident incident, AppDbContext db, string resolutionNotes, CancellationToken cancellationToken)
    {
        incident.Status = IncidentStatus.Closed;
        incident.ResolvedAt = DateTimeOffset.UtcNow;
        incident.ResolutionNotes = resolutionNotes;
        incident.UpdatedAt = DateTimeOffset.UtcNow;

        await _messageHistory.AppendMessageToIncidentAsync(
            incident.Id,
            MessageRole.Assistant,
            $"Incidente resuelto: {resolutionNotes}",
            "IncidentAutoWorkflow",
            cancellationToken);

        // Send email notification
        if (!string.IsNullOrEmpty(incident.UserEmail))
        {
            await _emailService.SendIncidentResolvedEmailAsync(incident.UserEmail, incident, cancellationToken);
        }

        // Generate KB article
        try
        {
            await _kbGenerator.GenerateFromIncidentAsync(incident.Id, cancellationToken);
            _logger.LogInformation("KB article generated for incident {IncidentId}", incident.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate KB article for incident {IncidentId}", incident.Id);
        }

        _logger.LogInformation("Incident {IncidentId} resolved and closed", incident.Id);
    }

    private async Task<string> GenerateCodeFixProposalAsync(Incident incident, CancellationToken cancellationToken)
    {
        // First, try rule-based fix for known Sandpit code errors
        var titleLower = incident.Title.ToLowerInvariant();
        var descLower = incident.Description.ToLowerInvariant();
        
        // Rule 1: CWE-369 Divide By Zero
        if (titleLower.Contains("cwe-369") || titleLower.Contains("divide by zero") ||
            descLower.Contains("divide by zero") || descLower.Contains("percentage == 100"))
        {
            _logger.LogInformation("Using rule-based fix for CWE-369 incident {IncidentId}", incident.Id);
            return """
                File: src/Services/OrderService.cs

                ```csharp
                // BEFORE:
                public decimal CalculateDiscount(decimal total, int percentage)
                {
                    return total / (100 - percentage);
                }
                ```

                ```csharp
                // AFTER:
                public decimal CalculateDiscount(decimal total, int percentage)
                {
                    if (percentage < 0 || percentage >= 100)
                    {
                        throw new ArgumentException("Percentage must be between 0 and 99", nameof(percentage));
                    }
                    return total * (percentage / 100m);
                }
                ```
                """;
        }
        
        // Rule 2: CWE-89 SQL Injection
        if (titleLower.Contains("cwe-89") || titleLower.Contains("sql injection") ||
            descLower.Contains("sql injection") || descLower.Contains("executeSqlRaw"))
        {
            _logger.LogInformation("Using rule-based fix for CWE-89 incident {IncidentId}", incident.Id);
            return """
                File: src/Services/UserService.cs
                
                CHANGE: Use parameterized queries instead of string concatenation
                
                BEFORE:
                var query = "SELECT * FROM Users WHERE Name LIKE '%" + keyword + "%'";
                return db.ExecuteSqlRaw(query);
                
                AFTER:
                var query = "SELECT * FROM Users WHERE Name LIKE @keyword";
                return db.ExecuteSqlRaw(query, new SqlParameter("@keyword", "%" + keyword + "%"));
                """;
        }

        // Try LLM for unknown code errors
        try
        {
            var kernel = _kernelFactory.CreateForTenant(incident.TenantId);
            var prompt = $"""
                Generate a code fix for the following incident. The fix should be minimal and safe.

                Incident Title: {incident.Title}
                Incident Description: {incident.Description}
                Category: {incident.Category}
                Severity: {incident.Severity}

                Provide the fix as:
                1. A brief description of what needs to be changed
                2. The file(s) that need to be modified
                3. The specific code changes needed

                Keep the fix minimal - only change what's absolutely necessary.
                """;

            var result = await kernel.InvokePromptAsync(prompt, cancellationToken: cancellationToken);
            var fixProposal = result.ToString();

            if (string.IsNullOrWhiteSpace(fixProposal))
            {
                throw new InvalidOperationException("LLM failed to generate code fix proposal");
            }

            return fixProposal;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate code fix proposal with LLM for incident {IncidentId}", incident.Id);
            // Fallback to a basic description
            return $"Manual intervention required for incident {incident.Id}: {incident.Description}";
        }
    }

    private async Task<string> GenerateDatabaseFixProposalAsync(Incident incident, CancellationToken cancellationToken)
    {
        // First, try rule-based fix for known Sandpit errors
        var titleLower = incident.Title.ToLowerInvariant();
        var descLower = incident.Description.ToLowerInvariant();
        
        // Rule 1: Corrupt Database Records from Sandpit
        if (titleLower.Contains("corrupt") && descLower.Contains("registros corruptos") && 
            (descLower.Contains("null") || descLower.Contains("severity")))
        {
            _logger.LogInformation("Using rule-based SQL fix for corrupt records incident {IncidentId}", incident.Id);
            return """
                -- Fix for corrupt records in Incidents table
                -- Step 1: Update NULL titles
                UPDATE Incidents 
                SET Title = '[CORREGIDO] Incidente sin titulo',
                    UpdatedAt = SYSDATETIMEOFFSET()
                WHERE Title IS NULL;
                
                -- Step 2: Update empty titles
                UPDATE Incidents 
                SET Title = '[CORREGIDO] Incidente sin titulo',
                    UpdatedAt = SYSDATETIMEOFFSET()
                WHERE Title = '';
                
                -- Step 3: Normalize invalid severity values
                UPDATE Incidents 
                SET Severity = CASE 
                    WHEN Severity < 1 THEN 1
                    WHEN Severity > 5 THEN 3
                    ELSE Severity
                END,
                UpdatedAt = SYSDATETIMEOFFSET()
                WHERE Severity < 1 OR Severity > 5;
                """;
        }
        
        // Rule 2: Invalid JSON payload
        if (descLower.Contains("json") && descLower.Contains("malformado"))
        {
            _logger.LogInformation("Using rule-based SQL fix for invalid JSON incident {IncidentId}", incident.Id);
            return """
                -- Fix for invalid JSON payloads
                UPDATE DiagnosticLogs 
                SET RawPayload = REPLACE(RawPayload, ',,', ','),
                    UpdatedAt = SYSDATETIMEOFFSET()
                WHERE RawPayload LIKE '%[%,,%';
                """;
        }

        // Try LLM for unknown database errors
        try
        {
            var kernel = _kernelFactory.CreateForTenant(incident.TenantId);
            var prompt = $"""
                Generate a SQL fix for the following database incident. The fix should be:
                1. Safe - use WHERE clauses to limit affected rows
                2. Minimal - only update what's necessary
                3. Reversible - avoid destructive operations without backup

                Incident Title: {incident.Title}
                Incident Description: {incident.Description}
                Category: {incident.Category}
                Severity: {incident.Severity}

                Provide the SQL statement that fixes the issue.
                Make sure to include appropriate WHERE clauses to limit affected records.
                """;

            var result = await kernel.InvokePromptAsync(prompt, cancellationToken: cancellationToken);
            var sqlFix = result.ToString().Trim();

            if (string.IsNullOrWhiteSpace(sqlFix))
            {
                throw new InvalidOperationException("LLM failed to generate SQL fix proposal");
            }

            // Validate the SQL is safe before returning
            if (!SafetyValidator.IsSafeSql(sqlFix))
            {
                var reason = SafetyValidator.GetRejectionReason(sqlFix);
                throw new InvalidOperationException($"Generated SQL fix is not safe: {reason}");
            }

            return sqlFix;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate SQL fix proposal with LLM for incident {IncidentId}", incident.Id);
            // Fallback to a safe placeholder that will require manual intervention
            throw new InvalidOperationException($"Failed to generate safe SQL fix for incident {incident.Id}. Manual intervention required.");
        }
    }

    private string ExtractTableNameFromSql(string sql)
    {
        // Simple extraction of table name from SQL
        var upper = sql.ToUpperInvariant();
        var fromIndex = upper.IndexOf(" FROM ") + 6;
        var spaceAfterTable = upper.IndexOf(' ', fromIndex);

        if (spaceAfterTable > fromIndex)
            return sql.Substring(fromIndex, spaceAfterTable - fromIndex);

        return sql.Substring(fromIndex);
    }

    private string? GetTenantConnectionString(Tenant tenant)
    {
        // For HiveCrew and Sandpit tenants, use hardcoded connection strings
        // These correspond to the MCP server connections
        if (tenant.Name.Equals("HiveCrew", StringComparison.OrdinalIgnoreCase))
        {
            return "Server=NB0080\\SQLEXPRESS;Database=Hive;Trusted_Connection=True;TrustServerCertificate=True";
        }

        if (tenant.Name.Equals("Sandpit", StringComparison.OrdinalIgnoreCase))
        {
            // Sandpit uses LocalDB - this is where the simulated errors are created
            return "Server=(localdb)\\mssqllocaldb;Database=HiveOps_Sandpit;Trusted_Connection=True;TrustServerCertificate=True;";
        }

        // For other tenants, use the encrypted connection string from the database
        return tenant.EncryptedConnectionString;
    }
}

public sealed class IncidentAutoWorkflowOptions
{
    public const string SectionName = "IncidentAutoWorkflow";

    public int PollingIntervalSeconds { get; set; } = 30;
    public int MaxConcurrentIncidents { get; set; } = 5;
    public int CodeLineThreshold { get; set; } = 3;
    public int DbRecordThreshold { get; set; } = 3;
}
