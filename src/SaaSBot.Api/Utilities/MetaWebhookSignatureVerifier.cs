using System.Security.Cryptography;
using System.Text;

namespace SaaSBot.Api.Utilities;

public static class MetaWebhookSignatureVerifier
{
    public static bool IsValid(string appSecret, byte[] body, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(appSecret) || body.Length == 0)
            return false;

        if (string.IsNullOrWhiteSpace(signatureHeader) || !signatureHeader.StartsWith("sha256=", StringComparison.Ordinal))
            return false;

        byte[] expectedSignature;
        try
        {
            expectedSignature = Convert.FromHexString(signatureHeader["sha256=".Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var computed = hmac.ComputeHash(body);
        return CryptographicOperations.FixedTimeEquals(computed, expectedSignature);
    }
}