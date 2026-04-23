#pragma warning disable SKEXP0001
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.Embeddings;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.AI;

namespace HiveOps.Infrastructure.AI;

public sealed class SemanticKernelEmbeddingService : IEmbeddingService
{
    private readonly KernelFactory _factory;
    private readonly SemanticKernelOptions _options;

    public SemanticKernelEmbeddingService(KernelFactory factory, IOptions<SemanticKernelOptions> options)
    {
        _factory = factory;
        _options = options.Value;
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_options.Embeddings.ApiKey) || string.IsNullOrWhiteSpace(_options.Embeddings.ModelId))
                return CreateDeterministicEmbedding(text);

            var kernel = _factory.CreateForTenant(Guid.Empty);
            var service = kernel.GetRequiredService<ITextEmbeddingGenerationService>();
            var result = await service.GenerateEmbeddingAsync(text, kernel, ct);
            return result.ToArray();
        }
        catch when (_options.Embeddings.UseDeterministicFallbackWhenUnavailable)
        {
            return CreateDeterministicEmbedding(text);
        }
    }

    public async Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        IEnumerable<string> texts, CancellationToken ct = default)
    {
        var items = texts.ToList();

        try
        {
            if (string.IsNullOrWhiteSpace(_options.Embeddings.ApiKey) || string.IsNullOrWhiteSpace(_options.Embeddings.ModelId))
                return items.Select(CreateDeterministicEmbedding).ToList();

            var kernel = _factory.CreateForTenant(Guid.Empty);
            var service = kernel.GetRequiredService<ITextEmbeddingGenerationService>();
            var results = await service.GenerateEmbeddingsAsync(items, kernel, ct);
            return results.Select(r => r.ToArray()).ToList();
        }
        catch when (_options.Embeddings.UseDeterministicFallbackWhenUnavailable)
        {
            return items.Select(CreateDeterministicEmbedding).ToList();
        }
    }

    private float[] CreateDeterministicEmbedding(string text)
    {
        var dimensions = Math.Max(32, _options.Embeddings.Dimensions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var vector = new float[dimensions];

        for (var i = 0; i < dimensions; i++)
        {
            var value = bytes[i % bytes.Length];
            vector[i] = (value / 127.5f) - 1f;
        }

        return vector;
    }
}
