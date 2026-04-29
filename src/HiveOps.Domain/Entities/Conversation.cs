using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;

namespace HiveOps.Domain.Entities;

public sealed class Conversation : ITenantScoped
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string ChannelUserId { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public ConversationStatus Status { get; set; } = ConversationStatus.Active;
    public int FailedClassificationCount { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ICollection<ConversationMessage> Messages { get; set; } = [];
}
