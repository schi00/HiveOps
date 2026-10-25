using System.Security.Cryptography;
using System.Text;

namespace HiveOps.Infrastructure.Multitenancy;

/// <summary>
/// Deterministic SHA-256 hash of a tenant API key. The hash is stored for
/// lookup (<c>Tenants.ApiKeyHash</c>) so the plaintext key is no longer needed
/// to resolve a tenant — a read of the table exposes digests, not live keys.
/// </summary>
public static class ApiKeyHasher
{
    public static string Compute(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
