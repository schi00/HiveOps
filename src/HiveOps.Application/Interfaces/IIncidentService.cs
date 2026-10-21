using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;

namespace HiveOps.Application.Interfaces;

/// <summary>
/// Service for creating and managing internal incidents.
/// </summary>
public interface IIncidentService
{
    /// <summary>
    /// Creates an internal incident (not originating from user conversation).
    /// </summary>
    Task<Incident> CreateInternalIncidentAsync(
        Guid tenantId,
        string title,
        string description,
        IncidentCategory category,
        IncidentSeverity severity,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default);
}
