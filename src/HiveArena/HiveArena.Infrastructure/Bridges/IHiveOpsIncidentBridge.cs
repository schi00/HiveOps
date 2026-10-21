using HiveArena.Core;

namespace HiveArena.Infrastructure.Bridges;

public interface IHiveOpsIncidentBridge
{
    Task<string?> ReportSyntheticIncidentAsync(IncidentBridgePayload payload, CancellationToken cancellationToken = default);
}
