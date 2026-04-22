using SaaSBot.Domain.Models;

namespace SaaSBot.Domain.Interfaces;

public interface IQueryNormalizer
{
    Task<QueryNormalizationResult> NormalizeAsync(string rawMessage, string tenantContext, CancellationToken cancellationToken = default);
}