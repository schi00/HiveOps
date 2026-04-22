using SaaSBot.Domain.Enums;

namespace SaaSBot.Domain.Models;

public sealed class ConversationStateContext
{
    public Guid ConversationId { get; set; }
    public Guid TenantId { get; set; }
    public ConversationState State { get; set; } = ConversationState.Idle;
    public ConversationState? PausedState { get; set; }
    public Dictionary<string, string> FlowData { get; set; } = [];
    public int FailedClassificationCount { get; set; }
    public bool HasGreeted { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
