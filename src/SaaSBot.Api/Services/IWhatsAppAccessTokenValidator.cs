namespace SaaSBot.Api.Services;

public interface IWhatsAppAccessTokenValidator
{
    Task<WhatsAppAccessTokenValidationResult> ValidateAsync(string accessToken, CancellationToken ct = default);
}

public sealed record WhatsAppAccessTokenValidationResult(
    bool IsValid,
    string? SubjectId,
    string? SubjectName,
    string? ErrorMessage);
