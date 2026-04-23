#pragma warning disable SKEXP0010
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using HiveOps.Domain.Interfaces;

namespace HiveOps.Infrastructure.AI;

/// <summary>
/// Creates a scoped Semantic Kernel Kernel instance with TenantId pre-injected
/// as a KernelArgument so all plugin invocations are tenant-aware.
/// </summary>
public sealed class KernelFactory
{
    private readonly IServiceProvider _provider;
    private readonly SemanticKernelOptions _options;

    public KernelFactory(IServiceProvider provider, IOptions<SemanticKernelOptions> options)
    {
        _provider = provider;
        _options = options.Value;
    }

    public Kernel CreateForTenant(Guid tenantId)
    {
        var builder = Kernel.CreateBuilder();
        var profile = _options.GetActiveProfile();

        if (!profile.Enabled)
            throw new InvalidOperationException($"SemanticKernel profile '{_options.ActiveProfile}' is disabled.");

        builder.AddOpenAIChatCompletion(
            modelId: profile.ModelId,
            apiKey: _options.OpenRouter.ApiKey,
            endpoint: new Uri(_options.OpenRouter.Endpoint),
            httpClient: CreateOpenRouterHttpClient());

        if (!string.IsNullOrWhiteSpace(_options.Embeddings.ApiKey) && !string.IsNullOrWhiteSpace(_options.Embeddings.ModelId))
        {
            builder.AddOpenAITextEmbeddingGeneration(
                modelId: _options.Embeddings.ModelId,
                apiKey: _options.Embeddings.ApiKey,
                dimensions: _options.Embeddings.Dimensions);
        }

        // Register the tenant-aware invocation filter
        builder.Services.AddSingleton<IFunctionInvocationFilter>(
            new TenantAwareKernelFilter(tenantId));

        // Register all agent plugins from the built service provider
        var kernel = builder.Build();
        return kernel;
    }

    private HttpClient CreateOpenRouterHttpClient()
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Add("X-Title", _options.OpenRouter.AppName);

        if (!string.IsNullOrWhiteSpace(_options.OpenRouter.Referer))
            httpClient.DefaultRequestHeaders.Add("HTTP-Referer", _options.OpenRouter.Referer);

        return httpClient;
    }
}
