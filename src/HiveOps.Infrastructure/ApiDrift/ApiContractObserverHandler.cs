using System.Net;
using System.Text;
using System.Text.Json;
using HiveOps.Application.ApiDrift;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HiveOps.Infrastructure.ApiDrift;

/// <summary>
/// DelegatingHandler that intercepts outgoing HTTP responses to detect API contract drift.
/// Compares the JSON schema hash of responses with the expected hash stored in ApiDependency.
/// </summary>
public sealed class ApiContractObserverHandler : DelegatingHandler
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ApiContractObserverHandler> _logger;
    private readonly JsonSchemaHasher _schemaHasher;

    public ApiContractObserverHandler(
        IServiceProvider serviceProvider,
        ILogger<ApiContractObserverHandler> logger,
        JsonSchemaHasher schemaHasher)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _schemaHasher = schemaHasher;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.Content?.Headers?.ContentType?.MediaType == "application/json")
        {
            await ProcessResponseFailSafeAsync(request, response, cancellationToken);
        }

        return response;
    }

    private async Task ProcessResponseFailSafeAsync(
        HttpRequestMessage request,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await ProcessResponseAsync(request, response, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[API_DRIFT] Fail-safe: Drift detection failed but response will continue. Endpoint: {Endpoint}",
                request.RequestUri);
        }
    }

    private async Task ProcessResponseAsync(
        HttpRequestMessage request,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();

        if (!tenantContext.IsResolved)
        {
            _logger.LogDebug("Tenant context not resolved, skipping API drift check");
            return;
        }

        var endpointUrl = request.RequestUri?.ToString() ?? "unknown";
        var provider = DetectProvider(endpointUrl);

        var dependency = await dbContext.ApiDependencies
            .FirstOrDefaultAsync(
                ad => ad.TenantId == tenantContext.TenantId &&
                      ad.EndpointUrl == endpointUrl,
                cancellationToken);

        // Read response content as stream for memory efficiency with large payloads
        var contentStream = await response.Content!.ReadAsStreamAsync(cancellationToken);
        
        // Buffer the content for both drift detection and subsequent reading
        using var memoryStream = new MemoryStream();
        await contentStream.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;

        // Parse JSON document from memory stream
        using var jsonDoc = await JsonDocument.ParseAsync(memoryStream, cancellationToken: cancellationToken);
        var currentHash = _schemaHasher.ComputeHash(jsonDoc.RootElement);
        var schemaSnapshot = _schemaHasher.ExtractSchema(jsonDoc.RootElement);

        // Reset stream for original consumer
        memoryStream.Position = 0;
        var originalContent = new StreamContent(memoryStream);
        foreach (var header in response.Content.Headers)
        {
            originalContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        response.Content = originalContent;

        if (dependency == null)
        {
            _logger.LogInformation(
                "[API_DRIFT] New endpoint detected in learning mode. Provider: {Provider}, Endpoint: {Endpoint}",
                provider, endpointUrl);

            dependency = new ApiDependency
            {
                TenantId = tenantContext.TenantId,
                ProviderName = provider,
                EndpointUrl = endpointUrl,
                HttpMethod = request.Method.Method,
                ExpectedSchemaHash = currentHash,
                LastResponseSchema = schemaSnapshot.Length > 4000 ? schemaSnapshot[..4000] : schemaSnapshot,
                LastChecked = DateTimeOffset.UtcNow,
                Status = ApiDependencyStatus.Learning
            };

            dbContext.ApiDependencies.Add(dependency);
            await dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "[API_DRIFT] Endpoint registered in learning mode. Hash: {Hash}",
                currentHash);
        }
        else
        {
            dependency.LastChecked = DateTimeOffset.UtcNow;
            dependency.LastResponseSchema = schemaSnapshot.Length > 4000 ? schemaSnapshot[..4000] : schemaSnapshot;

            if (dependency.ExpectedSchemaHash != currentHash)
            {
                _logger.LogWarning(
                    "[API_DRIFT] Contract drift detected! Provider: {Provider}, Endpoint: {Endpoint}, " +
                    "ExpectedHash: {ExpectedHash}, CurrentHash: {CurrentHash}",
                    provider, endpointUrl, dependency.ExpectedSchemaHash, currentHash);

                dependency.Status = ApiDependencyStatus.DriftDetected;
                await dbContext.SaveChangesAsync(cancellationToken);

                var driftEvent = new ApiContractChangedEvent(
                    tenantContext.TenantId,
                    provider,
                    endpointUrl,
                    dependency.ExpectedSchemaHash,
                    currentHash,
                    schemaSnapshot);

                await publisher.Publish(driftEvent, cancellationToken);
            }
            else
            {
                if (dependency.Status == ApiDependencyStatus.DriftDetected)
                {
                    dependency.Status = ApiDependencyStatus.Healthy;
                    _logger.LogInformation(
                        "[API_DRIFT] Endpoint recovered to healthy state. Provider: {Provider}, Endpoint: {Endpoint}",
                        provider, endpointUrl);
                }
                else if (dependency.Status == ApiDependencyStatus.Learning)
                {
                    dependency.Status = ApiDependencyStatus.Healthy;
                    _logger.LogInformation(
                        "[API_DRIFT] Endpoint graduated from learning mode. Provider: {Provider}, Endpoint: {Endpoint}",
                        provider, endpointUrl);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private static ApiProvider DetectProvider(string url)
    {
        var lowerUrl = url.ToLowerInvariant();
        if (lowerUrl.Contains("mercadolibre") || lowerUrl.Contains("mercadolivre") || lowerUrl.Contains("meli"))
            return ApiProvider.MercadoLibre;
        if (lowerUrl.Contains("shopify"))
            return ApiProvider.Shopify;
        if (lowerUrl.Contains("tiendanube") || lowerUrl.Contains("nuvemshop"))
            return ApiProvider.TiendaNube;
        return ApiProvider.Custom;
    }
}
