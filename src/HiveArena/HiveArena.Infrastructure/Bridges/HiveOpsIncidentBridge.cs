using System.Net.Http.Json;
using System.Text.Json;
using HiveArena.Core;
using HiveArena.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HiveArena.Infrastructure.Bridges;

/// <summary>
/// Reports synthetic incidents to HiveOps using the dev Sandpit-compatible endpoint with extended metadata.
/// </summary>
public sealed class HiveOpsIncidentBridge(
    IHttpClientFactory httpClientFactory,
    IOptions<HiveArenaBridgeOptions> options,
    ILogger<HiveOpsIncidentBridge> logger) : IHiveOpsIncidentBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<string?> ReportSyntheticIncidentAsync(IncidentBridgePayload payload, CancellationToken cancellationToken = default)
    {
        var opt = options.Value;
        if (string.IsNullOrWhiteSpace(opt.BaseUrl))
        {
            logger.LogWarning("HiveArena HiveOps bridge BaseUrl is not configured; skipping incident create.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(opt.DevKey))
        {
            logger.LogWarning("HiveArena HiveOps bridge DevKey is not configured; skipping incident create.");
            return null;
        }

        var client = httpClientFactory.CreateClient(nameof(HiveOpsIncidentBridge));
        client.DefaultRequestHeaders.Remove("X-Dev-Key");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Dev-Key", opt.DevKey);

        var meta = new
        {
            simulationRunId = payload.SimulationRunId,
            scenarioId = payload.ScenarioId,
            isSynthetic = payload.IsSynthetic,
            faultType = payload.FaultType.ToString(),
            seed = payload.Seed,
            arenaEnvironmentId = payload.ArenaEnvironmentId,
            runId = payload.RunId
        };

        var body = new
        {
            title = $"[HIVEARENA-SYNTHETIC] {payload.ScenarioId}",
            description = payload.HumanDescription + "\n\n```json\n" + JsonSerializer.Serialize(meta, JsonOptions) + "\n```",
            isSynthetic = true,
            simulationRunId = payload.SimulationRunId,
            scenarioId = payload.ScenarioId,
            faultType = payload.FaultType.ToString(),
            seed = payload.Seed,
            arenaEnvironmentId = payload.ArenaEnvironmentId
        };

        var url = $"{opt.BaseUrl.TrimEnd('/')}/api/support/incidents/from-sandpit";
        try
        {
            var response = await client.PostAsJsonAsync(url, body, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.TryGetGuid(out var incidentId))
                return incidentId.ToString();
            logger.LogInformation("HiveOps incident created but id not parsed from response.");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create HiveOps incident for scenario {ScenarioId}", payload.ScenarioId);
            return null;
        }
    }
}
