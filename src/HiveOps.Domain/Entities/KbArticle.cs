using HiveOps.Domain.Enums;
using HiveOps.Domain.Interfaces;

namespace HiveOps.Domain.Entities;

/// <summary>
/// Knowledge-base article generated from resolved incidents or curated manually.
/// Used to ground the LLM during triage so proven fixes are suggested first.
/// </summary>
public sealed class KbArticle : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? SourceIncidentId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Category { get; set; } = "Other"; // maps to IncidentCategory string
    public string Tags { get; set; } = string.Empty; // comma-separated for simple search
    public string ResolutionSteps { get; set; } = string.Empty;
    public bool IsPublished { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Tenant Tenant { get; set; } = null!;
}
