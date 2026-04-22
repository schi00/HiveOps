using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSBot.Domain.Interfaces;
using SaaSBot.Domain.Models;
using SaaSBot.Infrastructure.Cache;
using StackExchange.Redis;

namespace SaaSBot.Infrastructure.Cache;

public sealed class RedisConversationStateManager : IConversationStateManager
{
    private readonly IDatabase _redis;
    private readonly ILogger<RedisConversationStateManager> _logger;
    private readonly RedisOptions _options;

    private static string StateKey(Guid tenantId, Guid conversationId) =>
        $"tenant:{tenantId}:conv:{conversationId}:state";

    public RedisConversationStateManager(
        IConnectionMultiplexer connection,
        IOptions<RedisOptions> options,
        ILogger<RedisConversationStateManager> logger)
    {
        _redis = connection.GetDatabase();
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ConversationStateContext?> GetStateAsync(
        Guid tenantId, Guid conversationId, CancellationToken ct = default)
    {
        var key = StateKey(tenantId, conversationId);
        var raw = await _redis.StringGetAsync(key);
        if (!raw.HasValue) return null;

        return JsonSerializer.Deserialize<ConversationStateContext>(raw.ToString());
    }

    public async Task SetStateAsync(
        Guid tenantId, Guid conversationId, ConversationStateContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.LastUpdatedAt = DateTimeOffset.UtcNow;
        var key = StateKey(tenantId, conversationId);
        var json = JsonSerializer.Serialize(context);
        await _redis.StringSetAsync(key, json, _options.ConversationStateTtl);
    }

    public async Task PauseFlowAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default)
    {
        var ctx = await GetStateAsync(tenantId, conversationId, ct);
        if (ctx is null) return;

        ctx.PausedState = ctx.State;
        await SetStateAsync(tenantId, conversationId, ctx, ct);
    }

    public async Task ResumeFlowAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default)
    {
        var ctx = await GetStateAsync(tenantId, conversationId, ct);
        if (ctx is null || ctx.PausedState is null) return;

        ctx.State = ctx.PausedState.Value;
        ctx.PausedState = null;
        await SetStateAsync(tenantId, conversationId, ctx, ct);
    }

    public async Task ClearAsync(Guid tenantId, Guid conversationId, CancellationToken ct = default)
    {
        var key = StateKey(tenantId, conversationId);
        await _redis.KeyDeleteAsync(key);
    }
}
