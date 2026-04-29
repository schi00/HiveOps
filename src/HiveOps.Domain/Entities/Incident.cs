using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;

namespace HiveOps.Domain.Entities;

public sealed class Incident : ITenantScoped
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? ConversationId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IncidentCategory Category { get; set; } = IncidentCategory.Other;
    public IncidentSeverity Severity { get; set; } = IncidentSeverity.Low;
    public IncidentStatus Status { get; set; } = IncidentStatus.Open;
    public string AssignedTo { get; set; } = "bot";
    public string? ResolutionNotes { get; set; }
    public string? GitBranch { get; set; }
    public string? GitCommitHash { get; set; }
    public string? DeployStatus { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }

    // Auto workflow fields
    public string? UserEmail { get; set; }
    public bool RequiresHumanApproval { get; set; }
    public string? BackupLocation { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Conversation Conversation { get; set; } = null!;
    public ICollection<IncidentAttachment> Attachments { get; set; } = [];
    public ICollection<DiagnosticLog> DiagnosticLogs { get; set; } = [];
    public ICollection<ConversationMessage> Messages { get; set; } = [];
}
