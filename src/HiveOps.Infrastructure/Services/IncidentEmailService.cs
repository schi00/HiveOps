using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using HiveOps.Domain.Entities;

namespace HiveOps.Infrastructure.Services;

/// <summary>
/// Service to send email notifications for incident updates and resolutions.
/// </summary>
public sealed class IncidentEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<IncidentEmailService> _logger;

    public IncidentEmailService(IConfiguration config, ILogger<IncidentEmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Sends an email notification when an incident is updated.
    /// </summary>
    public async Task SendIncidentUpdateEmailAsync(
        string toEmail,
        Incident incident,
        CancellationToken cancellationToken = default)
    {
        var section = _config.GetSection("Email");
        var host = section["SmtpHost"];
        if (string.IsNullOrWhiteSpace(host))
        {
            _logger.LogWarning("Email:SmtpHost not configured, skipping incident update email");
            return;
        }

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
            Hola,

            Se ha actualizado el incidente "{incident.Title}" (ID: {incident.Id}).

            Estado: {incident.Status}
            Severidad: {incident.Severity}
            Categoría: {incident.Category}

            Descripción:
            {incident.Description}

            Última actualización: {incident.UpdatedAt:yyyy-MM-dd HH:mm:ss}

            Para más detalles, accede al dashboard de HiveOps.

            — Equipo HiveOps
            """;

        using var message = new MailMessage
        {
            From = new MailAddress(fromAddress, fromName),
            Subject = $"Actualización de incidente: {incident.Title}",
            Body = body,
            IsBodyHtml = false
        };
        message.To.Add(toEmail);

        try
        {
            await client.SendMailAsync(message, cancellationToken);
            _logger.LogInformation("Incident update email sent to {Email} for incident {IncidentId}", toEmail, incident.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send incident update email to {Email}", toEmail);
        }
    }

    /// <summary>
    /// Sends an email notification when an incident is resolved.
    /// </summary>
    public async Task SendIncidentResolvedEmailAsync(
        string toEmail,
        Incident incident,
        CancellationToken cancellationToken = default)
    {
        var section = _config.GetSection("Email");
        var host = section["SmtpHost"];
        if (string.IsNullOrWhiteSpace(host))
        {
            _logger.LogWarning("Email:SmtpHost not configured, skipping incident resolved email");
            return;
        }

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
            Hola,

            El incidente "{incident.Title}" (ID: {incident.Id}) ha sido resuelto.

            Severidad: {incident.Severity}
            Categoría: {incident.Category}
            Fecha de resolución: {incident.ResolvedAt:yyyy-MM-dd HH:mm:ss}

            Notas de resolución:
            {incident.ResolutionNotes ?? "No se proporcionaron notas de resolución."}

            El bot ha documentado este incidente en la base de conocimiento para futuras referencias.

            Si tienes alguna pregunta sobre esta resolución, no dudes en contactarnos.

            — Equipo HiveOps
            """;

        using var message = new MailMessage
        {
            From = new MailAddress(fromAddress, fromName),
            Subject = $"Incidente resuelto: {incident.Title}",
            Body = body,
            IsBodyHtml = false
        };
        message.To.Add(toEmail);

        try
        {
            await client.SendMailAsync(message, cancellationToken);
            _logger.LogInformation("Incident resolved email sent to {Email} for incident {IncidentId}", toEmail, incident.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send incident resolved email to {Email}", toEmail);
        }
    }
}
