using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;

namespace HiveOps.Application.Services;

/// <summary>
/// Service to manage conversation message history for incidents.
/// Appends messages to incidents and loads history for analysis.
/// </summary>
public interface IIncidentMessageHistoryService
{
    Task AppendMessageToIncidentAsync(
        Guid incidentId,
        MessageRole role,
        string content,
        string? agentName = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationMessage>> GetIncidentHistoryAsync(
        Guid incidentId,
        CancellationToken cancellationToken = default);

    Task<string> GetFormattedHistoryAsync(
        Guid incidentId,
        CancellationToken cancellationToken = default);
}
