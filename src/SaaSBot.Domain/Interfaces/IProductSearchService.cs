using SaaSBot.Domain.Models;

namespace SaaSBot.Domain.Interfaces;

public interface IProductSearchService
{
    Task<IReadOnlyList<ProductSearchResult>> HybridSearchAsync(
        Guid tenantId,
        string query,
        float[] queryEmbedding,
        int topK = 10,
        CancellationToken ct = default);
}
