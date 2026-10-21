namespace HiveArena.Infrastructure.ProvingGround;

/// <summary>
/// Lifecycle: provision → test → collect → destroy.
/// The environment is an isolated filesystem root; dependent services
/// (checkout, replay) place their artifacts inside <see cref="IEphemeralEnvironmentLease.RootPath"/>.
/// </summary>
public interface IEphemeralEnvironmentManager
{
    Task<IEphemeralEnvironmentLease> ProvisionAsync(string logicalName, CancellationToken cancellationToken = default);
}

public interface IEphemeralEnvironmentLease : IAsyncDisposable
{
    string ArenaEnvironmentId { get; }

    /// <summary>Isolated directory owned by this lease; deleted when the lease is disposed.</summary>
    string RootPath { get; }
}
