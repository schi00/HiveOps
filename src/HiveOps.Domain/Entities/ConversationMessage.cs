using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;

namespace HiveOps.Domain.Entities;

public sealed class ConversationMessage : ITenantScoped
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Guid? IncidentId { get; set; }  // Optional link to Incident
    public Guid TenantId { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? AgentName { get; set; }
    public string? ExternalMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // Navigation
    public Conversation Conversation { get; set; } = null!;
    public Incident? Incident { get; set; }
}
