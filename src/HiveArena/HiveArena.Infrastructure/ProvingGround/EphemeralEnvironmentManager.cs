using HiveArena.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HiveArena.Infrastructure.ProvingGround;

/// <summary>
/// Provisions an isolated filesystem root per environment and destroys it on dispose.
/// Future phases can swap this for Docker Compose / K8s job orchestration while
/// keeping the lease contract identical.
/// </summary>
public sealed class EphemeralEnvironmentManager(
    IOptions<ProvingGroundOptions> options,
    ILogger<EphemeralEnvironmentManager> logger) : IEphemeralEnvironmentManager
{
    public Task<IEphemeralEnvironmentLease> ProvisionAsync(string logicalName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var id = Guid.NewGuid().ToString("N");
        var root = Path.Combine(ResolveRoot(), "envs", $"{Sanitize(logicalName)}-{id}");
        Directory.CreateDirectory(root);

        logger.LogInformation(
            "HiveArena ephemeral environment provisioned: {ArenaEnvironmentId} ({LogicalName}) at {RootPath}",
            id, logicalName, root);

        return Task.FromResult<IEphemeralEnvironmentLease>(new Lease(id, root, logger));
    }

    private string ResolveRoot()
    {
        var configured = options.Value.WorkspacesRoot;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "hivearena")
            : configured;
    }

    private static string Sanitize(string logicalName)
    {
        var safe = new string(logicalName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return string.IsNullOrEmpty(safe) ? "env" : safe;
    }

    private sealed class Lease(string arenaEnvironmentId, string rootPath, ILogger logger) : IEphemeralEnvironmentLease
    {
        public string ArenaEnvironmentId => arenaEnvironmentId;
        public string RootPath => rootPath;

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!Directory.Exists(rootPath))
                    return ValueTask.CompletedTask;

                // git pack files and build outputs can be read-only; clear flags before delete.
                foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); }
                    catch { /* best effort */ }
                }

                Directory.Delete(rootPath, recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fully delete ephemeral environment at {RootPath}", rootPath);
            }

            logger.LogInformation("HiveArena ephemeral environment destroyed: {ArenaEnvironmentId}", arenaEnvironmentId);
            return ValueTask.CompletedTask;
        }
    }
}
