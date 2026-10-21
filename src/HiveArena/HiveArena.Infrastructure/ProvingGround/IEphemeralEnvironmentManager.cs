namespace HiveArena.Infrastructure.ProvingGround;

/// <summary>
/// Lifecycle: provision → test → collect → destroy (Compose/K8s in later phases).
/// </summary>
public interface IEphemeralEnvironmentManager
{
    Task<IEphemeralEnvironmentLease> ProvisionAsync(string logicalName, CancellationToken cancellationToken = default);
}

public interface IEphemeralEnvironmentLease : IAsyncDisposable
{
    string ArenaEnvironmentId { get; }
}
