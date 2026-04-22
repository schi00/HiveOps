namespace SaaSBot.Api.Services;

public interface IEmailService
{
    Task SendPasswordResetEmailAsync(string toEmail, string userName, string resetLink, CancellationToken ct = default);
}
