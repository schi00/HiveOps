using SaaSBot.Domain.Models;

namespace SaaSBot.Domain.Interfaces;

public interface IConversationStateManager
{
    Task<ConversationStateContext?> GetStateAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default);
    Task SetStateAsync(Guid tenantId, Guid conversationId, ConversationStateContext context, CancellationToken ct = default);
    Task PauseFlowAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default);
    Task ResumeFlowAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default);
    Task ClearAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default);
}
