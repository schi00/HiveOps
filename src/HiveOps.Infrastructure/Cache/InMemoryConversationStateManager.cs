using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;

namespace HiveOps.Infrastructure.Cache;

/// <summary>
/// In-process conversation state for ephemeral environments when Redis is not used.
/// </summary>
public sealed class InMemoryConversationStateManager : IConversationStateManager
{
    private readonly Dictionary<string, ConversationStateContext> _state = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public Task<ConversationStateContext?> GetStateAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default)
    {
        var key = BuildKey(tenantId, conversationId);
        lock (_sync)
        {
            if (!_state.TryGetValue(key, out var context))
                return Task.FromResult<ConversationStateContext?>(null);

            return Task.FromResult<ConversationStateContext?>(Clone(context));
        }
    }

    public Task SetStateAsync(Guid tenantId, Guid conversationId, ConversationStateContext context, CancellationToken ct = default)
    {
        var key = BuildKey(tenantId, conversationId);
        lock (_sync)
        {
            context.ConversationId = conversationId;
            context.TenantId = tenantId;
            context.LastUpdatedAt = DateTimeOffset.UtcNow;
            _state[key] = Clone(context);
        }

        return Task.CompletedTask;
    }

    public async Task PauseFlowAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default)
    {
        var current = await GetStateAsync(tenantId, conversationId, ct)
            ?? new ConversationStateContext { ConversationId = conversationId, TenantId = tenantId };
        current.PausedState = current.State;
        current.State = ConversationState.AwaitingHuman;
        await SetStateAsync(tenantId, conversationId, current, ct);
    }

    public async Task ResumeFlowAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default)
    {
        var current = await GetStateAsync(tenantId, conversationId, ct)
            ?? new ConversationStateContext { ConversationId = conversationId, TenantId = tenantId };
        current.State = current.PausedState ?? ConversationState.Idle;
        current.PausedState = null;
        await SetStateAsync(tenantId, conversationId, current, ct);
    }

    public Task ClearAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default)
    {
        var key = BuildKey(tenantId, conversationId);
        lock (_sync)
        {
            _state.Remove(key);
        }

        return Task.CompletedTask;
    }

    private static string BuildKey(Guid tenantId, Guid conversationId) => $"{tenantId:N}:{conversationId:N}";

    private static ConversationStateContext Clone(ConversationStateContext source)
        => new()
        {
            ConversationId = source.ConversationId,
            TenantId = source.TenantId,
            State = source.State,
            PausedState = source.PausedState,
            FailedClassificationCount = source.FailedClassificationCount,
            HasGreeted = source.HasGreeted,
            LastUpdatedAt = source.LastUpdatedAt,
            FlowData = source.FlowData.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
        };
}
