using System.Security.Cryptography;
using System.Text;
using HiveOps.Domain.Enums;

namespace HiveOps.Application.Support;

public static class IncidentFingerprint
{
    /// <summary>16-char hex fingerprint for dedupe within tenant.</summary>
    public static string Compute(IncidentCategory category, string description, string? extractedEntitiesJson)
    {
        var words = description.ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .Take(48);

        var normalized = string.Join(' ', words);
        var payload = $"{(int)category}|{normalized}|{extractedEntitiesJson ?? ""}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash.AsSpan(0, 8)); // 16 hex chars
    }
}
