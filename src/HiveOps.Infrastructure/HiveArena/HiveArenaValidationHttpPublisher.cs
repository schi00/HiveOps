using System.Net.Http.Json;
using System.Text.Json;
using HiveOps.Application.Configuration;
using HiveOps.Application.HiveArena;
using HiveOps.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HiveOps.Infrastructure.HiveArena;

public sealed class HiveArenaValidationHttpPublisher(
    IHttpClientFactory httpClientFactory,
    IOptions<HiveArenaPublisherOptions> options,
    ILogger<HiveArenaValidationHttpPublisher> logger) : IHiveArenaValidationPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task PublishAsync(HiveArenaValidationRequestedMessage message, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        if (!o.Enabled || string.IsNullOrWhiteSpace(o.HiveArenaBaseUrl))
        {
            logger.LogDebug("HiveArena validation publisher disabled or base URL missing; skipping.");
            return;
        }

        var client = httpClientFactory.CreateClient(nameof(HiveArenaValidationHttpPublisher));
        client.DefaultRequestHeaders.Remove("X-HiveArena-Key");
        if (!string.IsNullOrEmpty(o.HiveArenaApiKey))
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-HiveArena-Key", o.HiveArenaApiKey);

        var url = $"{o.HiveArenaBaseUrl.TrimEnd('/')}/api/hivearena/validation/requests";
        try
        {
            using var content = JsonContent.Create(message, options: JsonOptions);
            var response = await client.PostAsync(url, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            logger.LogInformation(
                "Published HiveArena validation request {RequestId} for incident {IncidentId}",
                message.RequestId,
                message.IncidentId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish HiveArena validation request {RequestId}", message.RequestId);
        }
    }
}
