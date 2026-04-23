using HiveOps.Domain.Models;

namespace HiveOps.Domain.Interfaces;

public interface IQueryNormalizer
{
    Task<QueryNormalizationResult> NormalizeAsync(string rawMessage, string tenantContext, CancellationToken cancellationToken = default);
}