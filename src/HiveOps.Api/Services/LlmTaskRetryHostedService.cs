using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using HiveOps.Application.Interfaces;
using HiveOps.Domain.Entities;
using HiveOps.Infrastructure.AI;
using HiveOps.Infrastructure.Persistence;

namespace HiveOps.Api.Services;

/// <summary>
/// Replays durable <see cref="LlmPendingTask"/> entries left behind when the LLM
/// was unreachable. Each due task is itself a liveness probe: while the provider
/// circuit is open only one task per cycle is attempted (cheap probe), and when
/// providers recover the backlog drains in batches with exponential backoff.
/// Config: HiveOps:LlmRetry:IntervalSeconds (30), :BatchSize (10).
/// </summary>
public sealed class LlmTaskRetryHostedService : BackgroundService
{
    private static readonly TimeSpan BackoffBase = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BackoffCap = TimeSpan.FromMinutes(45);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LlmTaskRetryHostedService> _logger;
    private readonly TimeSpan _interval;
    private readonly int _batchSize;

    public LlmTaskRetryHostedService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<LlmTaskRetryHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(
            Math.Max(5, configuration.GetValue<int?>("HiveOps:LlmRetry:IntervalSeconds") ?? 30));
        _batchSize = Math.Clamp(
            configuration.GetValue<int?>("HiveOps:LlmRetry:BatchSize") ?? 10, 1, 100);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let migrations/bootstrap settle before the first sweep.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LLM retry sweep failed; will retry next cycle.");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task RunSweepAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTimeOffset.UtcNow;
        var due = await db.LlmPendingTasks
            .IgnoreQueryFilters()
            .Where(t => t.Status == LlmPendingTask.StatusPending && t.NextAttemptAtUtc <= now)
            .OrderBy(t => t.NextAttemptAtUtc)
            .Take(FailoverChatCompletionService.CircuitOpen ? 1 : _batchSize)
            .ToListAsync(ct);

        foreach (var task in due)
        {
            if (ct.IsCancellationRequested)
                return;

            task.Attempts++;
            task.UpdatedAtUtc = DateTimeOffset.UtcNow;
            try
            {
                var succeeded = await DispatchAsync(scope.ServiceProvider, task, ct);
                if (succeeded)
                {
                    task.Status = LlmPendingTask.StatusCompleted;
                    task.CompletedAtUtc = DateTimeOffset.UtcNow;
                    task.LastError = null;
                    _logger.LogInformation(
                        "LLM task {TaskType} ({DedupeKey}) completed for tenant {TenantId} after {Attempts} attempt(s).",
                        task.TaskType, task.DedupeKey, task.TenantId, task.Attempts);
                }
                else
                {
                    Reschedule(task, "work not finished yet (LLM still down or output incomplete)");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "LLM task {TaskType} ({DedupeKey}) attempt {Attempts} failed for tenant {TenantId}.",
                    task.TaskType, task.DedupeKey, task.Attempts, task.TenantId);
                Reschedule(task, ex.Message);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private void Reschedule(LlmPendingTask task, string error)
    {
        if (task.Attempts >= task.MaxAttempts)
        {
            task.Status = LlmPendingTask.StatusDeadLetter;
            task.CompletedAtUtc = DateTimeOffset.UtcNow;
            task.LastError = $"dead-letter after {task.Attempts} attempts: {Truncate(error)}";
            _logger.LogError(
                "LLM task {TaskType} ({DedupeKey}) dead-lettered for tenant {TenantId}.",
                task.TaskType, task.DedupeKey, task.TenantId);
            return;
        }

        var delay = TimeSpan.FromTicks(Math.Min(
            BackoffBase.Ticks * (1L << Math.Min(task.Attempts, 10)),
            BackoffCap.Ticks));
        task.NextAttemptAtUtc = DateTimeOffset.UtcNow + delay;
        task.LastError = Truncate(error);
    }

    /// <summary>
    /// Replays one task. Returns true only when the underlying work is verifiably
    /// finished — a degraded re-run (LLM still down) reschedules instead of
    /// burning the item to "done".
    /// </summary>
    private async Task<bool> DispatchAsync(IServiceProvider sp, LlmPendingTask task, CancellationToken ct)
    {
        switch (task.TaskType)
        {
            case LlmPendingTask.TaskDocDistill:
            {
                var documentId = ReadGuid(task.PayloadJson, "documentId");
                if (documentId is null)
                    return true; // malformed payload: nothing to replay
                var packs = sp.GetRequiredService<ITenantContextPackService>();
                var doc = await packs.RetryDistillDocumentAsync(task.TenantId, documentId.Value, ct);
                return doc is null || string.Equals(doc.Status, "Distilled", StringComparison.OrdinalIgnoreCase);
            }

            case LlmPendingTask.TaskDbProfile:
            {
                var packs = sp.GetRequiredService<ITenantContextPackService>();
                var pack = await packs.RefreshDatabaseProfileAsync(task.TenantId, "retry-worker", ct);
                var profiledBy = TryParse(pack?.DatabaseProfileJson)?["profiledBy"]?.GetValue<string>();
                return string.Equals(profiledBy, "llm", StringComparison.OrdinalIgnoreCase);
            }

            case LlmPendingTask.TaskImprovementDraft:
            {
                var suggestionId = ReadGuid(task.PayloadJson, "suggestionId");
                if (suggestionId is null)
                    return true;
                var improvements = sp.GetRequiredService<ITenantImprovementService>();
                var suggestion = await improvements.GenerateDraftAsync(task.TenantId, suggestionId.Value, ct);
                return suggestion is null || DraftIsComplete(suggestion);
            }

            default:
                _logger.LogWarning("Unknown LLM task type {TaskType}; completing as no-op.", task.TaskType);
                return true;
        }
    }

    /// <summary>Draft counts as finished when the LLM filled the actionable fields of its kind.</summary>
    private static bool DraftIsComplete(TenantImprovementSuggestion suggestion)
    {
        var draft = TryParse(suggestion.DraftPayloadJson);
        if (draft is null)
            return false;

        return suggestion.Kind switch
        {
            "NewPlaybook" or "DataQualityRule" =>
                !string.IsNullOrWhiteSpace(draft["actionPayloadTemplate"]?.GetValue<string>()),
            "KbGap" or "PlannerGap" =>
                !string.IsNullOrWhiteSpace(draft["body"]?.GetValue<string>()),
            _ => true
        };
    }

    private static Guid? ReadGuid(string payloadJson, string key)
    {
        var node = TryParse(payloadJson)?[key];
        return node is JsonValue v && v.TryGetValue<Guid>(out var g) ? g : null;
    }

    private static JsonObject? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string Truncate(string value, int max = 400) =>
        value.Length <= max ? value : value[..max];
}
