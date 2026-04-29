using System.Security.Cryptography;
using System.Text;

namespace HiveOps.Infrastructure.Secrets;

public static class HmacValidator
{
    public static string ComputeSignature(string secret, byte[] payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsValid(string? secret, byte[] payload, DateTimeOffset timestamp, TimeSpan maxSkew, string? providedSignature)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(providedSignature))
            return false;

        var now = DateTimeOffset.UtcNow;
        if (timestamp < now - maxSkew || timestamp > now + maxSkew)
            return false;

        var expected = ComputeSignature(secret, payload);
        return FixedTimeEquals(expected, providedSignature);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var result = 0;
        for (int i = 0; i < a.Length; i++)
            result |= a[i] ^ b[i];
        return result == 0;
    }
}
