using System.Net;
using System.Net.Mail;

namespace HiveOps.Api.Services;

public sealed class SmtpEmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IConfiguration config, ILogger<SmtpEmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendPasswordResetEmailAsync(string toEmail, string userName, string resetLink, CancellationToken ct = default)
    {
        var section = _config.GetSection("Email");
        var host = section["SmtpHost"];
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("Email:SmtpHost no está configurado en appsettings.json.");

        var port = section.GetValue<int>("SmtpPort", 587);
        var enableSsl = section.GetValue<bool>("EnableSsl", true);
        var username = section["Username"] ?? string.Empty;
        var password = section["Password"] ?? string.Empty;
        var fromAddress = section["FromAddress"] ?? username;
        var fromName = section["FromName"] ?? "HiveOps";

#pragma warning disable CA5336
        using var client = new SmtpClient(host, port)
        {
            EnableSsl = enableSsl,
            Credentials = string.IsNullOrEmpty(username)
                ? null
                : new NetworkCredential(username, password),
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
#pragma warning restore CA5336

        var body = $"""
            Hola {userName},

            Recibimos una solicitud para restablecer la contraseña de tu cuenta en HiveOps.

            Hacé clic en el siguiente enlace para crear una nueva contraseña:

            {resetLink}

            Este enlace expira en 30 minutos.

            Si no solicitaste restablecer tu contraseña, ignorá este mensaje. Tu contraseña no cambiará.

            — Equipo HiveOps
            """;

        using var message = new MailMessage
        {
            From = new MailAddress(fromAddress, fromName),
            Subject = "Restablecé tu contraseña — HiveOps",
            Body = body,
            IsBodyHtml = false
        };
        message.To.Add(toEmail);

        await client.SendMailAsync(message, ct);
        _logger.LogInformation("Password reset email sent to {Email}", toEmail);
    }
}
