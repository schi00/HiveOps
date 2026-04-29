using HiveOps.Application.Services;
using HiveOps.Domain.Entities;
using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;
using HiveOps.Infrastructure.Multitenancy;
using HiveOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HiveOps.Infrastructure.Services;

/// <summary>
/// Implementation of incident message history service.
/// </summary>
public sealed class IncidentMessageHistoryService : IIncidentMessageHistoryService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public IncidentMessageHistoryService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Appends a message to the incident's message history.
    /// </summary>
    public async Task AppendMessageToIncidentAsync(
        Guid incidentId,
        MessageRole role,
        string content,
        string? agentName = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        // First, get the incident without tenant filter to determine the tenant
        var incident = await db.Incidents
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);

        if (incident is null)
            throw new InvalidOperationException($"Incident {incidentId} not found");

        // Set the tenant context for subsequent operations
        if (tenantContext is TenantContext tc && !tc.IsResolved)
        {
            tc.SetTenant(incident.TenantId);
        }

        // Ensure the incident has a ConversationId - create one if needed
        Guid conversationId;
        if (incident.ConversationId.HasValue)
        {
            // Check if conversation exists
            var conversationExists = await db.Conversations
                .IgnoreQueryFilters()
                .AnyAsync(c => c.Id == incident.ConversationId.Value, cancellationToken);
            
            if (conversationExists)
            {
                conversationId = incident.ConversationId.Value;
            }
            else
            {
                // Create a new conversation for this incident
                conversationId = Guid.NewGuid();
                var newConversation = new Conversation
                {
                    Id = conversationId,
                    TenantId = incident.TenantId,
                    Status = ConversationStatus.Active,
                    Channel = "api",
                    ChannelUserId = "system",
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastActivityAt = DateTimeOffset.UtcNow
                };
                db.Conversations.Add(newConversation);
                
                // Update incident with new conversation
                incident.ConversationId = conversationId;
                db.Entry(incident).State = EntityState.Modified;
            }
        }
        else
        {
            // Create a new conversation for this incident
            conversationId = Guid.NewGuid();
            var newConversation = new Conversation
            {
                Id = conversationId,
                TenantId = incident.TenantId,
                Status = ConversationStatus.Active,
                Channel = "api",
                ChannelUserId = "system",
                CreatedAt = DateTimeOffset.UtcNow,
                LastActivityAt = DateTimeOffset.UtcNow
            };
            db.Conversations.Add(newConversation);
            
            // Update incident with new conversation
            incident.ConversationId = conversationId;
            db.Entry(incident).State = EntityState.Modified;
        }

        var message = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            TenantId = incident.TenantId,
            IncidentId = incidentId,
            Role = role,
            Content = content,
            AgentName = agentName,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.ConversationMessages.Add(message);
        incident.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Loads the message history for an incident.
    /// </summary>
    public async Task<IReadOnlyList<ConversationMessage>> GetIncidentHistoryAsync(
        Guid incidentId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        // First, get the incident without tenant filter to determine the tenant
        var incidentNoFilter = await db.Incidents
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);

        if (incidentNoFilter is null)
            throw new InvalidOperationException($"Incident {incidentId} not found");

        // Set the tenant context for subsequent operations
        if (tenantContext is TenantContext tc && !tc.IsResolved)
        {
            tc.SetTenant(incidentNoFilter.TenantId);
        }

        // Now reload with messages using the tenant context
        var incident = await db.Incidents
            .Include(i => i.Messages)
            .FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);

        if (incident is null)
            throw new InvalidOperationException($"Incident {incidentId} not found after setting tenant context");

        return incident.Messages.OrderBy(m => m.CreatedAt).ToList();
    }

    /// <summary>
    /// Gets the conversation history as a formatted string for LLM analysis.
    /// </summary>
    public async Task<string> GetFormattedHistoryAsync(
        Guid incidentId,
        CancellationToken cancellationToken = default)
    {
        var messages = await GetIncidentHistoryAsync(incidentId, cancellationToken);

        if (messages.Count == 0)
            return "No conversation history available.";

        var formatted = new List<string>();
        foreach (var msg in messages)
        {
            var role = msg.Role.ToString();
            var agent = msg.AgentName is not null ? $" ({msg.AgentName})" : "";
            formatted.Add($"[{role}{agent}]: {msg.Content}");
        }

        return string.Join("\n", formatted);
    }
}
