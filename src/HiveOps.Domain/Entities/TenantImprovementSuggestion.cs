using HiveOps.Domain.Interfaces;

namespace HiveOps.Domain.Entities;

/// <summary>
/// Self-improvement suggestion queued for human review. Every agent failure
/// auto-files an entry (or bumps OccurrenceCount on a heuristic match); a
/// periodic/manual scan can also generate suggestions from failed incidents.
/// A suggestion carries evidence plus an LLM-drafted artifact payload and can
/// be converted into a real TenantPlaybook or dismissed.
/// </summary>
public sealed class TenantImprovementSuggestion : ITenantScoped
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Source incident that triggered this suggestion. Null for manually generated ones.</summary>
    public Guid? IncidentId { get; set; }

    /// <summary>"NewPlaybook" | "KbGap" | "DataQualityRule" | "PlannerGap"</summary>
    public string Kind { get; set; } = "NewPlaybook";

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>JSON object: { "symptoms": "...", "failedAttempts": [...], "escalationNotes": "...", "relatedTerms": [...] }</summary>
    public string EvidenceJson { get; set; } = "{}";

    /// <summary>JSON object with a proposed artifact. For NewPlaybook: { "code","name","triggerSymptoms","actionType","actionPayloadTemplate","requiredParameters":[],"verificationSql" }. For KbGap: { "title","body","tags":[] }. For DataQualityRule: { "rule","detectionSql","fixSql" }.</summary>
    public string DraftPayloadJson { get; set; } = "{}";

    /// <summary>"Suggested" | "Dismissed" | "Converted"</summary>
    public string Status { get; set; } = "Suggested";

    /// <summary>Bumped when a similar failure recurs (heuristic match by the service).</summary>
    public int OccurrenceCount { get; set; } = 1;

    /// <summary>Set when Converted: the TenantPlaybook.Id created from this suggestion.</summary>
    public Guid? ConvertedPlaybookId { get; set; }

    public string? CreatedBy { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAtUtc { get; set; }
}
