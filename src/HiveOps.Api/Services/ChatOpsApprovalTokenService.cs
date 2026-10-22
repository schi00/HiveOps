using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace HiveOps.Api.Services;

/// <summary>
/// Generates and validates stateless, time-limited HMAC-SHA256 tokens for 1-click
/// incident approval or rejection from Slack, Microsoft Teams, email, or mobile links.
/// </summary>
public sealed class ChatOpsApprovalTokenService
{
    private const string FallbackSigningKey = "hiveops-chatops-fallback-signing-key-2026";
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    private readonly byte[] _signingKeyBytes;

    public ChatOpsApprovalTokenService(IConfiguration? configuration = null)
    {
        var key = ResolveSigningKey(configuration);
        _signingKeyBytes = Encoding.UTF8.GetBytes(key);
    }

    public string CreateToken(Guid tenantId, Guid incidentId, string action, TimeSpan? ttl = null)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId cannot be empty.", nameof(tenantId));
        if (incidentId == Guid.Empty)
            throw new ArgumentException("IncidentId cannot be empty.", nameof(incidentId));

        var normalizedAction = NormalizeAction(action);
        if (normalizedAction is null)
            throw new ArgumentException("Action must be 'approve' or 'reject'.", nameof(action));

        var effectiveTtl = ttl ?? DefaultTtl;
        var expiresUnixSeconds = DateTimeOffset.UtcNow.Add(effectiveTtl).ToUnixTimeSeconds();
        var payload = BuildPayload(tenantId, incidentId, normalizedAction, expiresUnixSeconds);
        var signatureHex = ComputeSignatureHex(payload);

        return $"{expiresUnixSeconds}.{signatureHex}";
    }

    public bool TryValidateToken(
        Guid tenantId,
        Guid incidentId,
        string action,
        string? token,
        out string? errorReason)
    {
        if (tenantId == Guid.Empty || incidentId == Guid.Empty)
        {
            errorReason = "TenantId and IncidentId are required.";
            return false;
        }

        var normalizedAction = NormalizeAction(action);
        if (normalizedAction is null)
        {
            errorReason = "Invalid action. Allowed values are 'approve' or 'reject'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            errorReason = "Missing approval token.";
            return false;
        }

        var parts = token.Trim().Split('.');
        if (parts.Length != 2)
        {
            errorReason = "Malformed approval token.";
            return false;
        }

        if (!long.TryParse(parts[0], out var expiresUnixSeconds))
        {
            errorReason = "Invalid token expiration timestamp.";
            return false;
        }

        var providedSigHex = parts[1].Trim().ToLowerInvariant();
        var expectedPayload = BuildPayload(tenantId, incidentId, normalizedAction, expiresUnixSeconds);
        var expectedSigHex = ComputeSignatureHex(expectedPayload);

        var expectedBytes = Encoding.UTF8.GetBytes(expectedSigHex);
        var providedBytes = Encoding.UTF8.GetBytes(providedSigHex);

        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
        {
            errorReason = "Invalid token signature.";
            return false;
        }

        var nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (nowUnixSeconds > expiresUnixSeconds)
        {
            errorReason = "Approval token has expired.";
            return false;
        }

        errorReason = null;
        return true;
    }

    private static string? NormalizeAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return null;

        var normalized = action.Trim().ToLowerInvariant();
        return normalized is "approve" or "reject" ? normalized : null;
    }

    private static string BuildPayload(Guid tenantId, Guid incidentId, string normalizedAction, long expiresUnixSeconds)
        => $"{tenantId:N}|{incidentId:N}|{normalizedAction}|{expiresUnixSeconds}";

    private string ComputeSignatureHex(string payload)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(_signingKeyBytes, payloadBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ResolveSigningKey(IConfiguration? configuration)
    {
        var jwtKey = configuration?["Jwt:Key"];
        if (!string.IsNullOrWhiteSpace(jwtKey))
            return jwtKey;

        var adminKey = configuration?["Admin:ApiKey"];
        if (!string.IsNullOrWhiteSpace(adminKey))
            return adminKey;

        return FallbackSigningKey;
    }
}
