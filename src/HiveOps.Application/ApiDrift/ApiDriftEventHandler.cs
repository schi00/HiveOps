using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Application.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace HiveOps.Application.ApiDrift;

/// <summary>
/// Handles ApiContractChangedEvent by creating an internal incident for API drift.
/// </summary>
public sealed class ApiDriftEventHandler : INotificationHandler<ApiContractChangedEvent>
{
    private readonly IIncidentService _incidentService;
    private readonly ILogger<ApiDriftEventHandler> _logger;

    public ApiDriftEventHandler(
        IIncidentService incidentService,
        ILogger<ApiDriftEventHandler> logger)
    {
        _incidentService = incidentService;
        _logger = logger;
    }

    public async Task Handle(ApiContractChangedEvent notification, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[API_DRIFT] Processing drift event for {Provider} at {Endpoint}",
            notification.ProviderName, notification.EndpointUrl);

        var title = $"[API_DRIFT] Cambio detectado en {notification.ProviderName}";
        var description = $"""
            Se ha detectado un cambio en el contrato de API de {notification.ProviderName}.

            **Endpoint afectado:** {notification.EndpointUrl}
            **Hash esperado:** {notification.ExpectedSchemaHash}
            **Hash actual:** {notification.CurrentSchemaHash}
            **Detectado:** {notification.DetectedAt:yyyy-MM-dd HH:mm:ss UTC}

            **Nuevo esquema detectado:**
            ```
            {notification.SchemaSnapshot}
            ```

            Este incidente requiere revisión para adaptar las integraciones al nuevo esquema.
            """;

        var incident = await _incidentService.CreateInternalIncidentAsync(
            notification.TenantId,
            title,
            description,
            IncidentCategory.Infrastructure,
            IncidentSeverity.High,
            new[] { "api-drift", "infrastructure", notification.ProviderName.ToString().ToLowerInvariant() },
            cancellationToken);

        _logger.LogInformation(
            "[API_DRIFT] Incident created: {IncidentId} for tenant {TenantId}",
            incident.Id, notification.TenantId);
    }
}
