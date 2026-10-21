using HiveOps.Application.Interfaces;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HiveOps.Infrastructure.Services;

/// <summary>
/// Implementation of IIncidentService that creates internal incidents directly via AppDbContext.
/// </summary>
public sealed class IncidentService : IIncidentService
{
    private readonly AppDbContext _dbContext;
    private readonly ITenantContext _tenantContext;
    private readonly ISupervisionNotifier _notifier;

    public IncidentService(
        AppDbContext dbContext,
        ITenantContext tenantContext,
        ISupervisionNotifier notifier)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _notifier = notifier;
    }

    public async Task<Incident> CreateInternalIncidentAsync(
        Guid tenantId,
        string title,
        string description,
        IncidentCategory category,
        IncidentSeverity severity,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = null,
            Title = title,
            Description = description,
            Category = category,
            Severity = severity,
            Status = IncidentStatus.Open,
            AssignedTo = "bot",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.Incidents.Add(incident);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyIncidentCreatedAsync(
            tenantId,
            incident.Id,
            incident.Title,
            incident.Severity.ToString(),
            cancellationToken);

        return incident;
    }
}
