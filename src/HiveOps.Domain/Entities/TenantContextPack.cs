using HiveOps.Domain.Interfaces;

namespace HiveOps.Domain.Entities;

/// <summary>
/// Per-tenant knowledge pack that gives the AI engine durable context:
/// business glossary, database profile, repo profiles, and a ring buffer
/// of past resolution memory. One row per tenant; the service maintains a
/// condensed PromptDigest for cheap prompt injection.
/// </summary>
public sealed class TenantContextPack : ITenantScoped
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Optimistic version, incremented on every mutation.</summary>
    public int Version { get; set; } = 1;

    /// <summary>JSON object: { "term": { "meaning": "...", "tables": ["..."], "columns": ["..."] } } — business glossary mapping domain terms (e.g. "remito", "articulo") to data constructs. Capped ~300 entries by service.</summary>
    public string DomainGlossaryJson { get; set; } = "{}";

    /// <summary>JSON object: { "criticalTables": [...], "conventions": "...", "knownAnomalies": [...], "refreshedAtUtc": "..." }</summary>
    public string DatabaseProfileJson { get; set; } = "{}";

    /// <summary>JSON object: { "repos": [ { "name": "...", "modules": [...], "entryPoints": [...], "conventions": "..." } ] } — capped ~10 repos.</summary>
    public string RepoProfilesJson { get; set; } = "{}";

    /// <summary>JSON array ring buffer (&lt;=100 entries): [ { "symptom": "...", "entity": "...", "rootCause": "...", "fixThatWorked": "...", "fixThatFailed": "...", "succeeded": true, "atUtc": "..." } ]</summary>
    public string ResolutionMemoryJson { get; set; } = "[]";

    /// <summary>Pre-rendered condensed prompt text (&lt;=6000 chars) injected into investigation prompts. Rebuilt on every mutation by the service.</summary>
    public string PromptDigest { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }
}
