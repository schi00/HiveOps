using Microsoft.Extensions.Logging;

namespace HiveArena.Infrastructure.ProvingGround;

/// <summary>
/// MVP stub: records provision/destroy; replace with Docker Compose / K8s job orchestration.
/// </summary>
public sealed class EphemeralEnvironmentManager(ILogger<EphemeralEnvironmentManager> logger) : IEphemeralEnvironmentManager
{
    public Task<IEphemeralEnvironmentLease> ProvisionAsync(string logicalName, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        logger.LogInformation("HiveArena ephemeral environment provisioned: {ArenaEnvironmentId} ({LogicalName})", id, logicalName);
        return Task.FromResult<IEphemeralEnvironmentLease>(new Lease(id, logger));
    }

    private sealed class Lease(string arenaEnvironmentId, ILogger<EphemeralEnvironmentManager> logger) : IEphemeralEnvironmentLease
    {
        public string ArenaEnvironmentId => arenaEnvironmentId;

        public ValueTask DisposeAsync()
        {
            logger.LogInformation("HiveArena ephemeral environment destroyed: {ArenaEnvironmentId}", arenaEnvironmentId);
            return ValueTask.CompletedTask;
        }
    }
}
