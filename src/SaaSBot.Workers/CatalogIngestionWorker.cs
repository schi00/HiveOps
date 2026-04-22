using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SaaSBot.Agents.Ingestion;
using SaaSBot.Infrastructure.Persistence;

namespace SaaSBot.Workers;

/// <summary>
/// Background worker that processes queued catalog ingestion jobs.
/// Jobs are created when a tenant uploads a CSV via POST /api/tenants/{id}/catalog/upload.
/// Uses a simple in-memory channel. Replace with Redis Streams or Azure Service Bus for production.
/// </summary>
public sealed class CatalogIngestionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CatalogIngestionWorker> _logger;

    // Simple in-process channel — swap for Redis/ServiceBus in production
    private static readonly System.Threading.Channels.Channel<IngestionJob> _queue =
        System.Threading.Channels.Channel.CreateBounded<IngestionJob>(100);

    public CatalogIngestionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<CatalogIngestionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Enqueues a catalog ingestion job from the API layer.</summary>
    public static async Task EnqueueAsync(IngestionJob job, CancellationToken ct = default)
    {
        await _queue.Writer.WriteAsync(job, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CatalogIngestionWorker started.");

        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessJobAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown requested: this is an expected cancellation path.
            _logger.LogInformation("CatalogIngestionWorker stopping due to host cancellation.");
        }
    }

    private async Task ProcessJobAsync(IngestionJob job, CancellationToken ct)
    {
        _logger.LogInformation("Processing catalog for Tenant {TenantId}, File: {FileName}",
            job.TenantId, job.FileName);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var plugin = scope.ServiceProvider.GetRequiredService<IngestionPlugin>();

        try
        {
            await using var stream = new MemoryStream(job.FileBytes);
            var result = await plugin.ProcessCsvAsync(job.TenantId, stream, ct);

            _logger.LogInformation(
                "Ingestion complete for Tenant {TenantId}: {Inserted} inserted, {Updated} updated, {Errors} errors.",
                job.TenantId, result.Inserted, result.Updated, result.Errors.Count);

            if (result.HasErrors)
            {
                foreach (var err in result.Errors)
                    _logger.LogWarning("Ingestion error: {Error}", err);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Catalog ingestion canceled for Tenant {TenantId}.", job.TenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error processing catalog for Tenant {TenantId}", job.TenantId);
        }
    }
}

public sealed record IngestionJob(Guid TenantId, string FileName, byte[] FileBytes);
