using System.Net.Http.Headers;
using System.Text.Json;

namespace SaaSBot.Api.Services;

public sealed class MetaWhatsAppAccessTokenValidator : IWhatsAppAccessTokenValidator
{
    private const string MeUrl = "https://graph.facebook.com/v22.0/me?fields=id,name";
    private readonly HttpClient _httpClient;
    private readonly ILogger<MetaWhatsAppAccessTokenValidator> _logger;

    public MetaWhatsAppAccessTokenValidator(
        HttpClient httpClient,
        ILogger<MetaWhatsAppAccessTokenValidator> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<WhatsAppAccessTokenValidationResult> ValidateAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return new WhatsAppAccessTokenValidationResult(false, null, null, "Access token is required.");

        using var request = new HttpRequestMessage(HttpMethod.Get, MeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());

        using var response = await _httpClient.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorMessage = TryReadGraphError(payload) ?? $"Meta validation failed with HTTP {(int)response.StatusCode}.";
            _logger.LogWarning("WhatsApp token validation failed: {Message}", errorMessage);
            return new WhatsAppAccessTokenValidationResult(false, null, null, errorMessage);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;

            return new WhatsAppAccessTokenValidationResult(true, id, name, null);
        }
        catch (JsonException)
        {
            return new WhatsAppAccessTokenValidationResult(true, null, null, null);
        }
    }

    private static string? TryReadGraphError(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("error", out var error))
                return null;

            return error.TryGetProperty("message", out var message) ? message.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
