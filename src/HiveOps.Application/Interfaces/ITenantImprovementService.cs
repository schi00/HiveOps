using HiveOps.Domain.Entities;

namespace HiveOps.Application.Interfaces;

/// <summary>
/// Manages the per-tenant self-improvement queue: every agent failure
/// auto-files a TenantImprovementSuggestion (new playbook, KB gap, data
/// quality rule, planner gap). Humans review the queue, ask the service to
/// draft/refine artifact payloads via LLM, then convert a suggestion into a
/// real TenantPlaybook or dismiss it.
/// </summary>
public interface ITenantImprovementService
{
    /// <summary>List suggestions newest-first; status filter in {"Suggested","Dismissed","Converted"} (null = Suggested+Converted, i.e. actionable queue).</summary>
    Task<IReadOnlyList<TenantImprovementSuggestion>> ListAsync(Guid tenantId, string? status = null, CancellationToken ct = default);

    /// <summary>Scans the last N incidents that failed/escalated (memory entries succeeded=false in the context pack OR incidents assigned to human) and creates suggestions for those lacking one. Idempotent per IncidentId. Returns count created.</summary>
    Task<int> GenerateFromFailuresAsync(Guid tenantId, int takeLast = 50, CancellationToken ct = default);

    /// <summary>Auto-ticket: creates a Suggested entry for a failed incident unless one already exists for that IncidentId (then bumps OccurrenceCount). Never throws.</summary>
    Task<TenantImprovementSuggestion?> EnsureForFailedIncidentAsync(Guid tenantId, Incident incident, string? notes, CancellationToken ct = default);

    /// <summary>LLM-generates/refines the DraftPayloadJson for the suggestion. Returns updated suggestion or null if not found/not Suggested.</summary>
    Task<TenantImprovementSuggestion?> GenerateDraftAsync(Guid tenantId, Guid suggestionId, CancellationToken ct = default);

    /// <summary>Converts a Suggested entry into a real TenantPlaybook (RequiresApproval=true, inactive/disabled until human review). Sets Status=Converted + ConvertedPlaybookId. Returns created playbook or null if not convertible.</summary>
    Task<TenantPlaybook?> ConvertToPlaybookAsync(Guid tenantId, Guid suggestionId, string? decidedBy, CancellationToken ct = default);

    /// <summary>Sets Status to "Dismissed" or back to "Suggested". Returns updated suggestion or null if invalid.</summary>
    Task<TenantImprovementSuggestion?> SetStatusAsync(Guid tenantId, Guid suggestionId, string status, string? decidedBy, CancellationToken ct = default);
}
