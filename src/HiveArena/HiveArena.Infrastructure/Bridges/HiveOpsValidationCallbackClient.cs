using System.Net.Http.Json;
using System.Text.Json;
using HiveArena.Core;
using HiveArena.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HiveArena.Infrastructure.Bridges;

public interface IHiveOpsValidationCallbackClient
{
    Task NotifyCompletedAsync(HiveArenaValidationCompleted completed, CancellationToken cancellationToken = default);
}

public sealed class HiveOpsValidationCallbackClient(
    IHttpClientFactory httpClientFactory,
    IOptions<HiveOpsCallbackOptions> options,
    ILogger<HiveOpsValidationCallbackClient> logger) : IHiveOpsValidationCallbackClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task NotifyCompletedAsync(HiveArenaValidationCompleted completed, CancellationToken cancellationToken = default)
    {
        var opt = options.Value;
        if (string.IsNullOrWhiteSpace(opt.BaseUrl) || string.IsNullOrWhiteSpace(opt.SharedSecret))
        {
            logger.LogDebug("HiveOps validation callback not configured; skipping notify.");
            return;
        }

        var client = httpClientFactory.CreateClient(nameof(HiveOpsValidationCallbackClient));
        client.DefaultRequestHeaders.Remove("X-HiveArena-Key");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-HiveArena-Key", opt.SharedSecret);

        var url = $"{opt.BaseUrl.TrimEnd('/')}/api/internal/hivearena/validation-completed";
        try
        {
            using var content = JsonContent.Create(completed, options: JsonOptions);
            var response = await client.PostAsync(url, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            logger.LogInformation("HiveOps notified of validation completion {RequestId} outcome {Outcome}", completed.RequestId, completed.Outcome);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify HiveOps of validation completion {RequestId}", completed.RequestId);
        }
    }
}
